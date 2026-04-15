using System.Diagnostics;

namespace RevitFamilyBuilder.Services
{
    public class LoggingService
    {
        public void Log(string message)
        {
            Debug.WriteLine("[RevitFamilyBuilder] " + message);
        }
    }
}
