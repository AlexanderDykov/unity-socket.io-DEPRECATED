#region License
/*
 * SocketIO.cs
 *
 * The MIT License
 *
 * Copyright (c) 2014 Fabio Panettieri
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */

#endregion

//#define SOCKET_IO_DEBUG			// Uncomment this for debug
using System;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WebSocketSharp;

namespace SocketIO
{
	public class SocketIOComponent : MonoBehaviour
	{
		#region Public Properties

		public string url = "ws://127.0.0.1:4567/socket.io/?EIO=3&transport=websocket";
		public bool autoConnect = false;
		public int reconnectDelay = 5;
		public float ackExpirationTime = 30f;
		public float pingInterval = 25f;
		public float pingTimeout = 60f;

		public WebSocket Socket => _ws;
		public string Sid { get; set; }
		public bool IsConnected => _connected;

		#endregion

		#region Private Properties

		private volatile bool _connected;
		private volatile bool _thPinging;
		private volatile bool _thPong;
		private volatile bool _wsConnected;

		private Thread _socketThread;
		private Thread _pingThread;
		private WebSocket _ws;

		private Encoder _encoder;
		private Decoder _decoder;
		private Parser _parser;

		private Dictionary<string, List<Action<SocketIOEvent>>> _handlers;
		private List<Ack> _ackList;

		private int _packetId;

		private object _eventQueueLock;
		private Queue<SocketIOEvent> _eventQueue;

		private object _ackQueueLock;
		private Queue<Packet> _ackQueue;

		#endregion

		#if SOCKET_IO_DEBUG
		public Action<string> debugMethod;
		#endif

		#region Unity interface

		public void Awake()
		{
			_encoder = new Encoder();
			_decoder = new Decoder();
			_parser = new Parser();
			_handlers = new Dictionary<string, List<Action<SocketIOEvent>>>();
			_ackList = new List<Ack>();
			Sid = null;
			_packetId = 0;

			_ws = new WebSocket(url);
			_ws.OnOpen += OnOpen;
			_ws.OnMessage += OnMessage;
			_ws.OnError += OnError;
			_ws.OnClose += OnClose;
			_wsConnected = false;

			_eventQueueLock = new object();
			_eventQueue = new Queue<SocketIOEvent>();

			_ackQueueLock = new object();
			_ackQueue = new Queue<Packet>();

			_connected = false;

			#if SOCKET_IO_DEBUG
			if(debugMethod == null) { debugMethod = Debug.Log; };
			#endif
		}

		public void Start()
		{
			if (autoConnect) { Connect(); }
		}

		public void Update()
		{
			lock(_eventQueueLock){ 
				while(_eventQueue.Count > 0){
					EmitEvent(_eventQueue.Dequeue());
				}
			}

			lock(_ackQueueLock){
				while(_ackQueue.Count > 0){
					InvokeAck(_ackQueue.Dequeue());
				}
			}

			if(_wsConnected != _ws.IsConnected){
				_wsConnected = _ws.IsConnected;
				if(_wsConnected){
					EmitEvent("connect");
				} else {
					EmitEvent("disconnect");
				}
			}

			// GC expired acks
			if(_ackList.Count == 0) { return; }
			if(DateTime.Now.Subtract(_ackList[0].time).TotalSeconds < ackExpirationTime){ return; }
			_ackList.RemoveAt(0);
		}

		public void OnDestroy()
		{
			_socketThread?.Abort();
			_pingThread?.Abort();
		}

		public void OnApplicationQuit()
		{
			Close();
		}

		#endregion

		#region Public Interface

        public void SetHeader(string header, string value) {
            _ws.SetHeader(header, value);
        }
		
		public void Connect()
		{
			_connected = true;

			_socketThread = new Thread(RunSocketThread);
			_socketThread.Start(_ws);

			_pingThread = new Thread(RunPingThread);
			_pingThread.Start(_ws);
		}

		public void Close()
		{
			EmitClose();
			_connected = false;
		}

		public void On(string ev, Action<SocketIOEvent> callback)
		{
			if (!_handlers.ContainsKey(ev)) {
				_handlers[ev] = new List<Action<SocketIOEvent>>();
			}
			_handlers[ev].Add(callback);
		}

		public void Off(string ev, Action<SocketIOEvent> callback)
		{
			if (!_handlers.ContainsKey(ev)) {
				#if SOCKET_IO_DEBUG
				debugMethod.Invoke("[SocketIO] No callbacks registered for event: " + ev);
				#endif
				return;
			}

			var l = _handlers [ev];
			if (!l.Contains(callback)) {
				#if SOCKET_IO_DEBUG
				debugMethod.Invoke("[SocketIO] Couldn't remove callback action for event: " + ev);
				#endif
				return;
			}

			l.Remove(callback);
			if (l.Count == 0) {
				_handlers.Remove(ev);
			}
		}

		public void Emit(string ev)
		{
			EmitMessage(-1, $"[\"{ev}\"]");
		}

		public void Emit(string ev, Action<JToken> action)
		{
			EmitMessage(++_packetId, $"[\"{ev}\"]");
			_ackList.Add(new Ack(_packetId, action));
		}

		public void Emit(string ev, string str)
		{
			EmitMessage(-1, $"[\"{ev}\",\"{str}\"]");
		}

		public void Emit(string ev, JToken data)
		{
			EmitMessage(-1, $"[\"{ev}\",{data}]");
		}

		public void Emit(string ev, JToken data, Action<JToken> action)
		{
			EmitMessage(++_packetId, $"[\"{ev}\",{data}]");
			_ackList.Add(new Ack(_packetId, action));
		}

		#endregion

		#region Private Methods

		private void RunSocketThread(object obj)
		{
			WebSocket webSocket = (WebSocket)obj;
			while(_connected){
				if(webSocket.IsConnected){
					Thread.Sleep(reconnectDelay);
				} else {
					webSocket.Connect();
				}
			}
			webSocket.Close();
		}

		private void RunPingThread(object obj)
		{
			var webSocket = (WebSocket)obj;

			int timeoutMills = Mathf.FloorToInt(pingTimeout * 1000);
			int intervalMills = Mathf.FloorToInt(pingInterval * 1000);

			while(_connected)
			{
				if(!_wsConnected){
					Thread.Sleep(reconnectDelay);
				} else {
					_thPinging = true;
					_thPong =  false;
					
					EmitPacket(new Packet(EnginePacketType.PING));
					var pingStart = DateTime.Now;
					
					while(webSocket.IsConnected && _thPinging && (DateTime.Now.Subtract(pingStart).TotalSeconds < timeoutMills)){
						Thread.Sleep(200);
					}
					
					if(!_thPong){
						webSocket.Close();
					}

					Thread.Sleep(intervalMills);
				}
			}
		}

		private void EmitMessage(int id, string raw)
		{
			EmitPacket(new Packet(EnginePacketType.MESSAGE, SocketPacketType.EVENT, 0, "/", id, JToken.Parse(raw)));
		}

		private void EmitClose()
		{
			EmitPacket(new Packet(EnginePacketType.MESSAGE, SocketPacketType.DISCONNECT, 0, "/", -1, JValue.CreateString("")));
			EmitPacket(new Packet(EnginePacketType.CLOSE));
		}

		private void EmitPacket(Packet packet)
		{
			#if SOCKET_IO_DEBUG
			debugMethod.Invoke("[SocketIO] " + packet);
			#endif
			
			try {
				_ws.Send(_encoder.Encode(packet));
			} catch(SocketIOException ex) {
				#if SOCKET_IO_DEBUG
				debugMethod.Invoke(ex.ToString());
				#endif
			}
		}

		private void OnOpen(object sender, EventArgs e)
		{
			EmitEvent("open");
		}

		private void OnMessage(object sender, MessageEventArgs e)
		{
			#if SOCKET_IO_DEBUG
			debugMethod.Invoke("[SocketIO] Raw message: " + e.Data);
			#endif
			var packet = _decoder.Decode(e);

			switch (packet.enginePacketType) {
				case EnginePacketType.OPEN: 	HandleOpen(packet);		break;
				case EnginePacketType.CLOSE: 	EmitEvent("close");		break;
				case EnginePacketType.PING:		HandlePing();	   		break;
				case EnginePacketType.PONG:		HandlePong();	   		break;
				case EnginePacketType.MESSAGE: 	HandleMessage(packet);	break;
				case EnginePacketType.UNKNOWN:
					break;
				case EnginePacketType.UPGRADE:
					break;
				case EnginePacketType.NOOP:
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		private void HandleOpen(Packet packet)
		{
			#if SOCKET_IO_DEBUG
			debugMethod.Invoke("[SocketIO] Socket.IO sid: " + packet.json["sid"].str);
			#endif
			Sid = packet.json["sid"].ToString();
			EmitEvent("open");
		}

		private void HandlePing()
		{
			EmitPacket(new Packet(EnginePacketType.PONG));
		}

		private void HandlePong()
		{
			_thPong = true;
			_thPinging = false;
		}
		
		private void HandleMessage(Packet packet)
		{
			if(packet.json == null) { return; }

			switch (packet.socketPacketType)
			{
				case SocketPacketType.ACK:
				{
					for(int i = 0; i < _ackList.Count; i++){
						if(_ackList[i].packetId != packet.id){ continue; }
						lock(_ackQueueLock){ _ackQueue.Enqueue(packet); }
						return;
					}

#if SOCKET_IO_DEBUG
				debugMethod.Invoke("[SocketIO] Ack received for invalid Action: " + packet.id);
#endif
					break;
				}
				case SocketPacketType.EVENT:
				{
					SocketIOEvent e = _parser.Parse(packet.json);
					lock(_eventQueueLock){ _eventQueue.Enqueue(e); }

					break;
				}
			}
		}

		private void OnError(object sender, ErrorEventArgs e)
		{
			EmitEvent(new SocketIOEvent("error", JValue.CreateString(e.Message)));
		}

		private void OnClose(object sender, CloseEventArgs e)
		{
			EmitEvent("close");
		}

		private void EmitEvent(string type)
		{
			EmitEvent(new SocketIOEvent(type));
		}

		private void EmitEvent(SocketIOEvent ev)
		{
			if (!_handlers.ContainsKey(ev.Name)) { return; }
			foreach (Action<SocketIOEvent> handler in this._handlers[ev.Name]) {
				try{
					handler(ev);
				} catch(Exception ex){
					#if SOCKET_IO_DEBUG
					debugMethod.Invoke(ex.ToString());
					#endif
				}
			}
		}

		private void InvokeAck(Packet packet)
		{
			for(int i = 0; i < _ackList.Count; i++){
				if(_ackList[i].packetId != packet.id){ continue; }
				_ackList.RemoveAt(i);
				_ackList[i].Invoke(packet.json);
				return;
			}
		}

		#endregion
	}
}
