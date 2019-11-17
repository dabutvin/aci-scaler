using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Compute.v1;
using Google.Apis.Services;
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
        public const string Every24Hours = "0 30 9 * * *"; // 9:30 am every day
        private static HttpClient HttpClient = new HttpClient();

        /*
         * Checks queues and either starts or stops machines based on length of queues
         */
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

            var containerGroupNameSmall = Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL");
            if (string.IsNullOrEmpty(containerGroupNameSmall)) return;

            var azureToken = await GetAzureAccessToken();
            log.LogInformation("Got a token of length: " + azureToken.Length);

            var googleCredential = GetGoogleCredential(context);
            await compressImagesMessageQueue.FetchAttributesAsync();
            var messageCount = compressImagesMessageQueue.ApproximateMessageCount;
            log.LogInformation($"Found {messageCount} messages in compressImages");

            if (messageCount > 32)
            {
                await StartAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_MEDIUM"), log);
                await StartAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            }
            else if (messageCount > 0)
            {
                await StartAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            }
            else
            {
                await StopAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_MEDIUM"), log);
                await StopAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            }

            await longRunningCompressMessageQueue.FetchAttributesAsync();
            var longRunningMessageCount = longRunningCompressMessageQueue.ApproximateMessageCount;
            log.LogInformation($"Found {longRunningMessageCount} messages in longRunningCompressImages");

            if (longRunningMessageCount > 0)
            {
                // await StartAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_LARGE"), log);
                await StartGoogleInstance(
                    googleCredential,
                    Environment.GetEnvironmentVariable("GOOGLE_PROJECT"),
                    Environment.GetEnvironmentVariable("GOOGLE_ZONE"),
                    Environment.GetEnvironmentVariable("GOOGLE_INSTANCE"),
                    log);
            }
            else if (messageCount <= 0)
            {
                //await StopAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_LARGE"), log);
                await StopGoogleInstance(
                    googleCredential,
                    Environment.GetEnvironmentVariable("GOOGLE_PROJECT"),
                    Environment.GetEnvironmentVariable("GOOGLE_ZONE"),
                    Environment.GetEnvironmentVariable("GOOGLE_INSTANCE"),
                    log);
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
            var containerGroupNameSmall = Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL");
            if (string.IsNullOrEmpty(containerGroupNameSmall)) return;

            var azureToken = await GetAzureAccessToken();
            log.LogInformation("Got a token of length: " + azureToken.Length);

            var googleCredential = GetGoogleCredential(context);

            await StopAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_SMALL"), log);
            await StopAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_MEDIUM"), log);
            // await StopAciContainer(azureToken, Environment.GetEnvironmentVariable("CONTAINERGROUP_NAME_LARGE"), log);
            await StopGoogleInstance(
                googleCredential,
                Environment.GetEnvironmentVariable("GOOGLE_PROJECT"),
                Environment.GetEnvironmentVariable("GOOGLE_ZONE"),
                Environment.GetEnvironmentVariable("GOOGLE_INSTANCE"),
                log);
            log.LogInformation("Finished midday shutdown");
        }

        /*
         * This function is meant to stop, wait, and restart arbitrary ACI instances
         */
        [FunctionName("DailyRestartFunction")]
        public static async Task DailyRestartTrigger(
            [TimerTrigger(Every24Hours, RunOnStartup = true)] TimerInfo timerInfo,
            ExecutionContext context,
            ILogger log)
        {
            log.LogInformation("Starting daily restart");
            var dailyRestartName = Environment.GetEnvironmentVariable("DAILYRESTART_NAME");
            if (string.IsNullOrEmpty(dailyRestartName)) return;

            var azureToken = await GetAzureAccessToken();
            log.LogInformation("Got a token of length: " + azureToken.Length);

            await StopAciContainer(azureToken, dailyRestartName, log);
            log.LogInformation("stopped");
            await StartAciContainer(azureToken, dailyRestartName, log);
            log.LogInformation("started");
        }

        private static async Task<string> GetAzureAccessToken()
        {
            var response = await HttpClient
                .PostAsync(
                    $"https://login.microsoftonline.com/{Environment.GetEnvironmentVariable("TENANT_ID")}/oauth2/token",
                    new FormUrlEncodedContent(new[]
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

        private static GoogleCredential GetGoogleCredential(ExecutionContext context)
        {
            var json = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIAL");
            return GoogleCredential.FromJson(json).CreateScoped(ComputeService.Scope.Compute);
        }

        private static async Task StartAciContainer(string token, string containerGroup, ILogger log)
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

        private static async Task StopAciContainer(string token, string containerGroup, ILogger log)
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

        private static async Task StartGoogleInstance(GoogleCredential credential, string project, string zone, string instance, ILogger log)
        {
            log.LogInformation("Starting Google Instance " + instance);
            var service = new ComputeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential
            });

            var startRequest = service.Instances.Start(project, zone, instance);

            await startRequest.ExecuteAsync();
            log.LogInformation("Started Google Instance " + instance);
        }

        private static async Task StopGoogleInstance(GoogleCredential credential, string project, string zone, string instance, ILogger log)
        {
            log.LogInformation("Stopping Google Instance " + instance);
            var service = new ComputeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential
            });

            var stopRequest = service.Instances.Stop(project, zone, instance);
            await stopRequest.ExecuteAsync();
            log.LogInformation("Stopped Google Instance " + instance);
        }
    }
}
