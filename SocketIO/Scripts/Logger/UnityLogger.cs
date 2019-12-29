using System;
using UnityEngine;

namespace Logger
{
    public class UnityLogger : ILogger
    {
        public void Log(string message)
        {
            Debug.Log(message);
        }

        public void LogException(Exception exception)
        {
            Debug.LogException(exception);
        }
    }
}