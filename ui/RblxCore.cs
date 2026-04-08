using System;
using System.Text;

namespace RblxExecutorUI
{
    public static class RblxCore
    {
        private static string _lastError = "Core runtime is not included in the public repository.";

        public static bool Initialize()
        {
            _lastError = "Initialize unavailable in public build.";
            return false;
        }

        public static uint FindRobloxProcess()
        {
            _lastError = "Process discovery unavailable in public build.";
            return 0;
        }

        public static bool Connect(uint pid)
        {
            _lastError = "Connect unavailable in public build.";
            return false;
        }

        public static void Disconnect() { }

        public static uint GetRobloxPid() => 0;

        public static void RedirConsole() { }

        public static UIntPtr GetDataModel() => UIntPtr.Zero;

        public static int GetJobCount() => 0;

        public static bool GetClientInfo(StringBuilder buffer, int maxSize)
        {
            buffer.Clear();
            buffer.Append("Public build: runtime not included");
            return true;
        }

        public static string GetLastError() => _lastError;
    }
}
