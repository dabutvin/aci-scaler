using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace AciScaler
{
    public static class Scaler
    {
        public const string EveryFiveMinutes = "0 */5 * * * *";

        [FunctionName("ScalerFunction")]
        public static async Task Trigger([TimerTrigger(EveryFiveMinutes, RunOnStartup = true)]TimerInfo timerInfo, ILogger log)
        {
            log.LogInformation("Starting scaler function");

        }
    }
}
