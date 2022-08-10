using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Deepeyes.Functions.Models;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;

// TODO: use singleton to get vision client;

namespace Deepeyes.Functions
{


    public static class AnalyzeInput
    {
        private static Lazy<ComputerVisionClient> lazyClient = new Lazy<ComputerVisionClient>(InitializeComputerVisionClient);
        private static ComputerVisionClient ComputerVisionClient => lazyClient.Value;

        private static ComputerVisionClient InitializeComputerVisionClient()
        {
            return new(new ApiKeyServiceClientCredentials(Environment.GetEnvironmentVariable("ComputerVisionApiKey")), Array.Empty<System.Net.Http.DelegatingHandler>())
            {
                Endpoint = Environment.GetEnvironmentVariable("ComputerVisionEndpoint")
            }; ;
        }


        [FunctionName("AnalyzeInput")]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var myBlobName = context.GetInput<string>();

            // Replace "hello" with the name of your Durable Activity Function.
            var scanVisionResult = await context.CallActivityAsync<ScanVisionResult>("AnalyzeInput_DescribeImage", myBlobName);
            await context.CallActivityAsync("AnalyzeInput_SaveResult", scanVisionResult);
            if (scanVisionResult.Ocr.State == "PENDING")
            {
                scanVisionResult.Ocr.State = "RUNNING";
                await context.CallActivityAsync("AnalyzeInput_SaveResult", scanVisionResult);
                var operationId = await context.CallActivityAsync<string>("AnalyzeInput_StartExtractText", myBlobName);
                var textResult = await context.CallActivityAsync<IList<ReadResult>>("AnalyzeInput_ReceiveExtractedText", operationId);

                scanVisionResult.Ocr.State = "DONE";
                scanVisionResult.Ocr.Lines = textResult.SelectMany(x => x.Lines.Select(l => l.Text)).ToList();
                await context.CallActivityAsync("AnalyzeInput_SaveResult", scanVisionResult);
            }

        }

        [FunctionName("AnalyzeInput_DescribeImage")]
        public static async Task<ScanVisionResult> DescribeImage([ActivityTrigger] string myBlobName, [Blob("raw-pics/{myBlobName}", FileAccess.Read)] BlobClient myBlob, ILogger log)
        {
            // send the blob to vision api and get the results

            List<VisualFeatureTypes?> features = new() {
                VisualFeatureTypes.Tags,
                VisualFeatureTypes.Adult,
                VisualFeatureTypes.Categories,
                VisualFeatureTypes.Color,
                VisualFeatureTypes.Description,
                VisualFeatureTypes.Brands,
                VisualFeatureTypes.Faces,
                VisualFeatureTypes.ImageType,
                VisualFeatureTypes.Objects,
            };
            var result = await ComputerVisionClient.AnalyzeImageAsync(myBlob.Uri.ToString(), visualFeatures: features);

            var id = Guid.NewGuid().ToString();

            var tags = result.Tags.Select(t => new Tag { Name = t.Name, Confidence = t.Confidence }).ToList();
            var captions = result.Description.Captions.Select(caption => new Caption
            {
                Text = caption.Text,
                Confidence = caption.Confidence
            }).ToList();

            var faces = result.Faces.Select(face => new Face { Age = face.Age, Gender = face.Gender.ToString() }).ToList();

            var objects = result.Objects.Select(obj => new Models.Object
            {
                Name = obj.ObjectProperty,
                Confidence = obj.Confidence
            });

            bool hasText = tags.Any(t => t.Name == "text");

            return new ScanVisionResult
            {
                Id = id,
                Image = myBlob.Name,
                Tags = tags,
                Captions = captions,
                AccentColor = result.Color.AccentColor,
                DominantColors = result.Color.DominantColors.ToList(),
                Faces = faces,
                Objects = objects.ToList(),
                IsAdult = result.Adult.IsAdultContent,
                IsGory = result.Adult.IsGoryContent,
                IsRacy = result.Adult.IsRacyContent,
                AdultScore = result.Adult.AdultScore,
                GoreScore = result.Adult.GoreScore,
                RacyScore = result.Adult.RacyScore,
                Ocr = new Ocr
                {
                    State = hasText ? "PENDING" : "NONE"
                }
            };
        }

        [FunctionName("AnalyzeInput_StartExtractText")]
        public static async Task<string> AnalyzeInput_StartExtractText([ActivityTrigger] string myBlobName, [Blob("raw-pics/{myBlobName}", FileAccess.Read)] BlobClient myBlob, ILogger log)
        {
            log.LogInformation("Extracting text from image");

            // send the blob to vision api and get the results

            var textHeaders = await ComputerVisionClient.ReadAsync(myBlob.Uri.ToString());

            string operationLocation = textHeaders.OperationLocation;

            const int numberOfCharsInOperationId = 36;
            var operationId = operationLocation[^numberOfCharsInOperationId..];
            return operationId;

        }

        [FunctionName("AnalyzeInput_ReceiveExtractedText")]
        public static async Task<IList<ReadResult>> AnalyzeInput_ReceiveExtractedText([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            log.LogInformation("Extracting text from image");
            var operationId = context.GetInput<string>();

            ReadOperationResult results;
            do
            {
                results = await ComputerVisionClient.GetReadResultAsync(Guid.Parse(operationId));
                Thread.Sleep(1000);

            } while (results.Status == OperationStatusCodes.Running || results.Status == OperationStatusCodes.NotStarted);

            var textResults = results.AnalyzeResult.ReadResults;

            return textResults;

        }

        [FunctionName("AnalyzeInput_SaveResult")]
        [return: CosmosDB(
                   databaseName: "DeepEyesDB",
                   collectionName: "ScanVisionResults",
                   ConnectionStringSetting = "CosmosDBConnection")]
        public static ScanVisionResult SaveResult([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            log.LogInformation("Saving result");
            return context.GetInput<ScanVisionResult>();
        }

        [FunctionName("AnalyzeInput_BlobStart")]
        public static async Task BlobStart(
            [BlobTrigger("raw-pics/{name}")] BlobClient myBlob,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("AnalyzeInput", input: myBlob.Name);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'. {myBlob.Name}");

        }
    }
}