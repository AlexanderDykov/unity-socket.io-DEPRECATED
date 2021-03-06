﻿#region License
/*
 * Packet.cs
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

using Newtonsoft.Json.Linq;

namespace SocketIO
{
	public class Packet
	{
		public EnginePacketType enginePacketType;
		public SocketPacketType socketPacketType;

		public readonly int attachments;
		public string nsp;
		public int id;
		public JToken json;

		public Packet() : this(EnginePacketType.UNKNOWN) { }

		public Packet(EnginePacketType enginePacketType, SocketPacketType socketPacketType = SocketPacketType.UNKNOWN, int attachments = -1, string nsp = "/", int id = -1, JToken json = null)
		{
			this.enginePacketType = enginePacketType;
			this.socketPacketType = socketPacketType;
			this.attachments = attachments;
			this.nsp = nsp;
			this.id = id;
			this.json = json;
		}

		public override string ToString()
		{
			return
				$"[Packet: enginePacketType={enginePacketType}, socketPacketType={socketPacketType}, attachments={attachments}, nsp={nsp}, id={id}, json={json}]";
		}
	}
}
