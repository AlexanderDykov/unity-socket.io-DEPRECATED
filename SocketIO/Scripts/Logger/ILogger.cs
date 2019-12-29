using System;

namespace Logger
{
    public interface ILogger
    {
        void Log(string message);
        void LogException(Exception exception);
    }
}