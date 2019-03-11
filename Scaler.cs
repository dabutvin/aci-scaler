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
        private static HttpClient HttpClient = new HttpClient();

        [FunctionName("ScalerFunction")]
        public static async Task Trigger(
            [TimerTrigger(EveryMinute, RunOnStartup = true)] TimerInfo timerInfo,
            [Queue("compressimagesmessage")] CloudQueue compressImagesMessageQueues,
            ExecutionContext context,
            ILogger log)
        {
            log.LogInformation("==================================================================");
            log.LogInformation("Starting scaler function");
            log.LogInformation("==================================================================");

            var token = await GetAccessToken();
            log.LogInformation("Got a token of length: " + token.Length);

            await compressImagesMessageQueues.FetchAttributesAsync();
            var messageCount = compressImagesMessageQueues.ApproximateMessageCount;
            log.LogInformation($"Found {messageCount} messages in compressImages");
            if (messageCount > 0)
            {
                await StartContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME"), log);
            }
            else
            {
                await StopContainer(token, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME"), log);
            }
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
