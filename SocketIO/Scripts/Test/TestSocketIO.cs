#region License
/*
 * TestSocketIO.cs
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

using System.Collections;
using Logger;
using SocketIO;
using UnityEngine;

public class TestSocketIO : MonoBehaviour
{
	private SocketIO.SocketIO socket = new SocketIO.SocketIO("localhost", 3000, new UnityLogger());

	public void Start() 
	{
//		GameObject go = GameObject.Find("SocketIO");
//		socket = go.GetComponent<SocketIOComponent>();

		socket.On("open", TestOpen);
		socket.On("boop", TestBoop);
		socket.On("error", TestError);
		socket.On("close", TestClose);
		
		socket.On("move", OnMove);
		
		socket.Connect();
		
//		StartCoroutine("BeepBoop");
	}

	private void OnMove(SocketIOEvent obj)
	{
		Debug.Log(GetVectorFromJson(obj));
		Debug.Log(obj.Data["id"]);
//		var player = spawner.GetPlayer();
//		var navPos = player.GetComponent<Navigator>();
	}
	
	private static Vector3 GetVectorFromJson(SocketIOEvent obj)
	{
		return new Vector3(obj.Data["x"].ToObject<int>(), 0, obj.Data["y"].ToObject<int>());
	}

	public void TestOpen(SocketIOEvent e)
	{
		Debug.Log("[SocketIO] Open received: " + e.Name + " " + e.Data);
		socket.Emit("beep");
	}

	IEnumerator Test()
	{
		yield return new WaitForSeconds(2f);
		Debug.Log("cccc");
		socket.Close();
	}
	
	public void TestBoop(SocketIOEvent e)
	{
		Debug.Log("[SocketIO] Boop received: " + e.Name + " " + e.Data);
		StartCoroutine(Test());
//
//		if (e.Data == null) { return; }
//
//		Debug.Log(
//			"#####################################################" +
//			"THIS: " + e.Data["this"] +
//			"#####################################################"
//		);
	}
	
	public void TestError(SocketIOEvent e)
	{
		Debug.Log("[SocketIO] Error received: " + e.Name + " " + e.Data);
	}
	
	public void TestClose(SocketIOEvent e)
	{	
		Debug.Log("[SocketIO] Close received: " + e.Name + " " + e.Data);
	}
	
	private void Update()
	{
		socket.Update();
	}

	public void OnDestroy()
	{
		socket.Close();
	}
}
