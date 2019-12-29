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
using System;
using System.Collections.Generic;
using System.Threading;
using NativeWebSocket;
using Newtonsoft.Json.Linq;
using ILogger = Logger.ILogger;

namespace SocketIO
{
    public class SocketIO : IDisposable
    {
        private readonly WebSocket _websocket;
        private readonly Decoder _decoder;
        private readonly Encoder _encoder;
        private readonly Parser _parser;
        private readonly ILogger _logger;
        private readonly Dictionary<string, List<Action<SocketIOEvent>>> _handlers;
        private int _packetId;
        private readonly List<Ack> _ackList;

        private Thread _pingThread;
        private Thread _socketThread;
        private volatile bool _connected;
        private volatile bool _thPinging;
        private volatile bool _thPong;
        private volatile bool _wsConnected;

        private const int ReconnectDelay = 5;
        private const float AckExpirationTime = 30f;
        private const float PingInterval = 25f;
        private const float PingTimeout = 60f;

        private readonly object _eventQueueLock;
        private readonly Queue<SocketIOEvent> _eventQueue;
        
        private readonly object _ackQueueLock;
        private readonly Queue<Packet> _ackQueue;
        
        public string Sid { get; private set; }
        public bool IsConnected => _websocket.State == WebSocketState.Open || _websocket.State == WebSocketState.Closing;

        public SocketIO(string host, int port, ILogger logger)
        {
            _logger = logger;
            _websocket = new WebSocket($"ws://{host}:{port}/socket.io/?EIO=4&transport=websocket");
            _encoder = new Encoder(logger);
            _decoder = new Decoder(logger);
            _parser = new Parser();
            _handlers = new Dictionary<string, List<Action<SocketIOEvent>>>();
            _ackList = new List<Ack>();
            _packetId = 0;
            Sid = null;
            _connected = false;
            
            _eventQueueLock = new object();
            _eventQueue = new Queue<SocketIOEvent>();
            
            _ackQueueLock = new object();
            _ackQueue = new Queue<Packet>();

            InitCallbacks();
        }

        private void InitCallbacks()
        {
            _websocket.OnError += OnError;
            _websocket.OnMessage += OnMessage;
            _websocket.OnClose += OnClose;
        }

        private void RemoveCallbacks()
        {
            _websocket.OnError -= OnError;
            _websocket.OnMessage -= OnMessage;
            _websocket.OnClose -= OnClose;
        }
        
        private void OnClose(WebSocketCloseCode closecode)
        {
            EmitEvent("close");
        }
        
        private void OnError(string errorMessage)
        {
            EmitEvent(new SocketIOEvent("error", JValue.CreateString(errorMessage)));
        }
        
        private void OnMessage(byte[] data)
        {
            var packet = _decoder.Decode(data);
            
            switch (packet.enginePacketType) {
                case EnginePacketType.OPEN: 	
                    HandleOpen(packet);
                    break;
                case EnginePacketType.CLOSE:
                    EmitEvent("close");
                    break;
                case EnginePacketType.MESSAGE:
                    HandleMessage(packet);	
                    break;
                case EnginePacketType.PING:
                    HandlePing();
                    break;
                case EnginePacketType.PONG:
                    HandlePong();
                    break;
            }
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
        
        private void RunSocketThread(object obj)
        {
            WebSocket webSocket = (WebSocket)obj;
            while(_connected){
                if(_websocket.State != WebSocketState.Closed){
                    Thread.Sleep(ReconnectDelay);
                } else {
                    webSocket.Connect();
                }
            }
            webSocket.Close();
        }
        
        private void RunPingThread(object obj)
        {
            var webSocket = (WebSocket)obj;

            int timeoutMills = (int) Math.Floor(PingTimeout * 1000);
            int intervalMills = (int) Math.Floor(PingInterval * 1000);

            while(_connected)
            {
                if(!_wsConnected){
                    Thread.Sleep(ReconnectDelay);
                } else {
                    _thPinging = true;
                    _thPong =  false;
					
                    EmitPacket(new Packet(EnginePacketType.PING));
                    var pingStart = DateTime.Now;
					
                    while(IsConnected && _thPinging && (DateTime.Now.Subtract(pingStart).TotalSeconds < timeoutMills)){
                        Thread.Sleep(200);
                    }
					
                    if(!_thPong){
                        webSocket.Close();
                    }

                    Thread.Sleep(intervalMills);
                }
            }
        }
        
        private void HandleOpen(Packet packet)
        {
#if SOCKET_IO_DEBUG
            _logger?.Log("[SocketIO] Socket.IO sid: " + packet.json["sid"]);
#endif
            Sid = packet.json["sid"].ToString();
            EmitEvent("open");
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

			if(_wsConnected != IsConnected)
			{
				_wsConnected = IsConnected;
				EmitEvent(_wsConnected ? "connect" : "disconnect");
			}

			// GC expired acks
			if(_ackList.Count == 0) { return; }
			if(DateTime.Now.Subtract(_ackList[0].time).TotalSeconds < AckExpirationTime){ return; }
			_ackList.RemoveAt(0);
        }
        
        private void HandleMessage(Packet packet)
        {
            if(packet.json == null) { return; }

            switch (packet.socketPacketType)
            {
                case SocketPacketType.ACK:
                {
                    for (int i = 0; i < _ackList.Count; i++)
                    {
                        if (_ackList[i].packetId != packet.id)
                        {
                            continue;
                        }

                        lock (_ackQueueLock)
                        {
                            _ackQueue.Enqueue(packet);
                        }

                        return;
                    }

#if SOCKET_IO_DEBUG
                    _logger?.Log("[SocketIO] Ack received for invalid Action: " + packet.id);
#endif
                    break;
                }
                case SocketPacketType.EVENT:
                {
                    SocketIOEvent e = _parser.Parse(packet.json);
                    lock (_eventQueueLock)
                    {
                        _eventQueue.Enqueue(e);
                    }

                    break;
                }
            }
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
                    _logger?.LogException(ex);
#endif
                }
            }
        }
        
        public void Emit(string ev)
        {
            EmitMessage(-1, $"[\"{ev}\"]");
        }
        
        private void EmitPacket(Packet packet)
        {
#if SOCKET_IO_DEBUG
            _logger?.Log("[SocketIO] " + packet);
#endif
            try {
                _websocket.SendText(_encoder.Encode(packet));
            } catch(SocketIOException ex) {
#if SOCKET_IO_DEBUG
                _logger?.LogException(ex);
#endif
            }
        }
        private void EmitMessage(int id, string raw)
        {
            EmitPacket(new Packet(EnginePacketType.MESSAGE, SocketPacketType.EVENT, 0, "/", id, JToken.Parse(raw)));
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
                _logger?.Log("[SocketIO] No callbacks registered for event: " + ev);
#endif
                return;
            }

            var l = _handlers [ev];
            if (!l.Contains(callback)) {
#if SOCKET_IO_DEBUG
                _logger?.Log("[SocketIO] Couldn't remove callback action for event: " + ev);
#endif
                return;
            }

            l.Remove(callback);
            if (l.Count == 0) {
                _handlers.Remove(ev);
            }
        }
        
        private void InvokeAck(Packet packet)
        {
            for(var i = 0; i < _ackList.Count; i++){
                if(_ackList[i].packetId != packet.id){ continue; }
                _ackList.RemoveAt(i);
                _ackList[i].Invoke(packet.json);
                return;
            }
        }
        
        private void EmitClose()
        {
            EmitPacket(new Packet(EnginePacketType.MESSAGE, SocketPacketType.DISCONNECT, 0, "/", -1, JValue.CreateString("")));
            EmitPacket(new Packet(EnginePacketType.CLOSE));
        }
        
        public void Close()
        {
            EmitClose();
            _connected = false;
        }
        
        public void Connect()
        {
            _connected = true;
            
            _socketThread = new Thread(RunSocketThread);
            _socketThread.Start(_websocket);
            
            _pingThread = new Thread(RunPingThread);
            _pingThread.Start(_websocket);
        }

        public void Dispose()
        {
            _ackList.Clear();
            _handlers.Clear();
            RemoveCallbacks();
        }
    }
}