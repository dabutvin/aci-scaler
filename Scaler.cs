using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;

namespace AciScaler
{
    public static class Scaler
    {
        public const string EveryMinute = "0 */1 * * * *";
        public const string Every12Hours = "0 0 */12 * * *";
        private static HttpClient HttpClient = new HttpClient();

        [FunctionName("ScalerFunction")]
        public static async Task Trigger(
            [TimerTrigger(EveryMinute, RunOnStartup = true)] TimerInfo timerInfo,
            [Queue("compressimagesmessage")] CloudQueue compressImagesMessageQueue,
            [Queue("longrunningcompressmessage")] CloudQueue longRunningCompressMessageQueue,
            ExecutionContext context,
            ILogger log)
        {
            log.LogInformation("==================================================================");
            log.LogInformation("Starting scaler function");
            log.LogInformation("==================================================================");

            var token = await GetAccessToken();
            log.LogInformation("Got a token of length: " + token.Length);

            await compressImagesMessageQueue.FetchAttributesAsync();
            var messageCount = compressImagesMessageQueue.ApproximateMessageCount;
            log.LogInformation($"Found {messageCount} messages in compressImages");

            if (messageCount > 32)
            {
                await StartContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_MEDIUM"), log);
                await StartContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            }
            else if (messageCount > 0)
            {
                await StartContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            }
            else
            {
                await StopContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_MEDIUM"), log);
                await StopContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            }

            await longRunningCompressMessageQueue.FetchAttributesAsync();
            var longRunningMessageCount = longRunningCompressMessageQueue.ApproximateMessageCount;
            log.LogInformation($"Found {longRunningMessageCount} messages in longRunningCompressImages");

            if (longRunningMessageCount > 0)
            {
                await StartContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_LARGE"), log);
            }
            else
            {
                await StopContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_LARGE"), log);
            }
        }

        /*
         * We shut them down every once in a while as an auto-heal
         * These container groups can get stuck in a crashed state that does no work, but runs the meter
         * This also makes sure we get a fresh disk every once in a while in case we get low on space
         * all the actions are based on queue messages, so the message will unlock and retry if they were in the middle of anything
         */
        [FunctionName("MiddayShutdownFunction")]
        public static async Task ShutdownTrigger(
            [TimerTrigger(Every12Hours)] TimerInfo timerInfo,
            ExecutionContext context,
            ILogger log)
        {
            log.LogInformation("Starting midday shutdown");
            var token = await GetAccessToken();
            log.LogInformation("Got a token of length: " + token.Length);

            await StopContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            await StopContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_MEDIUM"), log);
            await StopContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_LARGE"), log);
            log.LogInformation("Finished midday shutdown");
        }

        private static async Task<string> GetAccessToken()
        {
            var response = await HttpClient
                .PostAsync(
                    $"https://login.microsoftonline.com/{Environment.GetEnvironmentVariable("TENANT_ID")}/oauth2/token",
                    new FormUrlEncodedContent(new []
                    {
                        KeyValuePair.Create("grant_type", "client_credentials"),
                        KeyValuePair.Create("client_id", Environment.GetEnvironmentVariable("CLIENT_ID")),
                        KeyValuePair.Create("client_secret", Environment.GetEnvironmentVariable("CLIENT_SECRET")),
                        KeyValuePair.Create("resource", "https://management.azure.com/"),
                    }));
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeAnonymousType(json, new { access_token = "" });
            return result.access_token;
        }

        private static async Task StartContainer(string token, string containerGroup, ILogger log)
        {
            log.LogInformation("Starting Container " + containerGroup);
            var httpRequestMessage = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://management.azure.com/subscriptions/{Environment.GetEnvironmentVariable("SUBSCRIPTION_ID")}/resourceGroups/" +
                $"{Environment.GetEnvironmentVariable("RESOURCEGROUP_NAME")}/providers/Microsoft.ContainerInstance/containerGroups/" +
                $"{containerGroup}/start?api-version=2018-10-01");

            httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await HttpClient.SendAsync(httpRequestMessage);
            await response.Content.ReadAsStringAsync();
            log.LogInformation("Started Container " + containerGroup);
        }

        private static async Task StopContainer(string token, string containerGroup, ILogger log)
        {
            log.LogInformation("Stopping Container " + containerGroup);
            var httpRequestMessage = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://management.azure.com/subscriptions/{Environment.GetEnvironmentVariable("SUBSCRIPTION_ID")}/resourceGroups/" +
                $"{Environment.GetEnvironmentVariable("RESOURCEGROUP_NAME")}/providers/Microsoft.ContainerInstance/containerGroups/" +
                $"{containerGroup}/stop?api-version=2018-10-01");

            httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await HttpClient.SendAsync(httpRequestMessage);
            await response.Content.ReadAsStringAsync();
            log.LogInformation("Stopped Container " + containerGroup);
        }
    }
}
