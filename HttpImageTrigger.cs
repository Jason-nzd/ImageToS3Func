using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.Runtime;
using ImageMagick;

public static class ImageMakeTransparent
{
    // Client singleton and variables for S3
    static IAmazonS3 s3client;
    static readonly string s3bucket = "supermarketimages";
    static readonly RegionEndpoint region = RegionEndpoint.APSoutheast2;

    [FunctionName("ImageMakeTransparentTrigger")]
    public static async Task<IActionResult> Run(

    // Trigger off http GET requests
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
    ILogger log)
    {
        // Get query parameters from http trigger
        string fileName = req.Query["fileName"];
        string url = req.Query["url"];
        // string widthString = req.Query["width"];
        // string heightString = req.Query["height"];
        // string qualityString = req.Query["quality"];

        // If query parameters are not valid, return error Message
        if (fileName == null || url == null || fileName.Length < 3 || !url.Contains("http"))
        {
            return new OkObjectResult(
                "This function requires 'fileName' and 'url' query parameters\n " +
                "example: /HttpImageTrigger?fileName=12345&url=http://domain.com/filename.jpg"
            );
        }

        // Todo: handle width, height, quality queries
        // ValidateHeightWidth();

        // Build a consolidated message string, which will be added to with other functions
        string consolidatedMsg = "";


        // Establish connection to S3, return if unable to connect
        var connectResponse = await ConnectToS3();
        if (!connectResponse.Succeeded)
            return new BadRequestObjectResult(connectResponse.Message);


        // Check S3 if file already exists, return if already exists
        fileName = CleanFileName(fileName);
        var existsResponse = await ImageAlreadyExistsOnS3(fileName, log);
        if (existsResponse.Succeeded) return new OkObjectResult(existsResponse.Message);


        // Download from url, return if unsuccessful
        var downloadResponse = await DownloadImageUrlToStream(url, log);
        if (downloadResponse.Succeeded)
            consolidatedMsg += downloadResponse.Message;
        else
            return new BadRequestObjectResult(downloadResponse.Message);


        // Load stream into ImageMagick for conversion, return if unsuccessful
        Stream outStream = new MemoryStream();
        var imResponse = MakeImageTransparent(downloadResponse.bytePayload, outStream, log);
        if (imResponse.Succeeded)
            consolidatedMsg += imResponse.Message + "\n\n";
        else
            return new BadRequestObjectResult(imResponse.Message);


        // Upload stream to S3, return final consolidated message, or unsuccessful upload msg
        var s3Response = await UploadStreamToS3(fileName, outStream, log);
        if (s3Response.Succeeded)
            return new OkObjectResult(consolidatedMsg + s3Response.Message);
        else
            return new BadRequestObjectResult(s3Response.Message);
    }

    private static async Task<Response> DownloadImageUrlToStream(string url, ILogger log)
    {
        try
        {
            // Download image using httpclient
            HttpClient httpclient = new HttpClient();
            byte[] downloadedBytes = await httpclient!.GetByteArrayAsync(url);

            // Log filename and dispose httpclient
            log.LogWarning(ExtractFileNameFromUrl(url));
            httpclient.Dispose();

            // Return Response with success, message, and byte[] payload
            return new Response(
                true,
                $"Original Image: {ExtractFileNameFromUrl(url)}\n" +
                    $"Size: {printFileSize(downloadedBytes.LongLength)}\n\n",
                downloadedBytes
            );
        }
        catch (System.Exception e)
        {
            return new Response(
                false,
                url + " was unable to be downloaded" + e.ToString()
            );
        }
    }

    private static async Task<Response> ConnectToS3()
    {
        try
        {
            // Connect to S3 using credentials from env
            string accessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY");
            string secretKey = Environment.GetEnvironmentVariable("AWS_SECRET_KEY");
            BasicAWSCredentials credentials = new BasicAWSCredentials(accessKey, secretKey);
            s3client = new AmazonS3Client(credentials, region);

            // Test S3 connection is valid by attempting to get test.webp
            await s3client.GetBucketLocationAsync(s3bucket);

            // If no exceptions have been thrown, we have successfully connected to S3
            return new Response(true);
        }
        catch (System.Exception e)
        {
            if (e.Message.StartsWith("The specified bucket does not exist"))
            {
                return new Response(
                    false,
                    $"Unable to connect to S3. The bucket: {s3bucket} does not exist and needs " +
                    "to be manually created."
                );
            }
            return new Response(
                false,
                "Unable to connect to S3. Check credentials were set as environment settings:\n" +
                "\"AWS_ACCESS_KEY\": \"<your aws access key>\",\n" +
                "\"AWS_SECRET_KEY\": \"<your aws secret key>\"\n" +
                e.Message + "\n" + e.ToString()
            );
        }
    }

    private static Response MakeImageTransparent(
        byte[] input,
        Stream outStream,
        ILogger log,
        int width = 200,
        int height = 200,
        int quality = 75)
    {
        try
        {
            using (var image = new MagickImage(input))
            {
                // Converts white pixels into transparent pixels with a fuzz of 3
                image.ColorFuzz = new Percentage(3);
                image.Alpha(AlphaOption.Set);
                image.BorderColor = MagickColors.White;
                image.Border(1);
                image.Settings.FillColor = MagickColors.Transparent;
                image.FloodFill(MagickColors.Transparent, 1, 1);
                image.Shave(1, 1);

                // Sets the output format to a default of 200x200 webp
                image.Scale(width, height);
                image.Quality = quality;
                image.Format = MagickFormat.WebP;

                // Write the image to outStream
                image.Write(outStream);

                // If image conversion is successful, log Message
                long imageByteLength = outStream.Length;

                return new Response(
                    true,
                    "Converted to Transparent Image with dimensions: " +
                    $"{width}x{height} \nSize: {printFileSize(imageByteLength)}"
                );
            }
        }
        catch (System.Exception e)
        {
            log.LogError(e.ToString());
            return new Response(false, "Unable to be processed by ImageMagick");
        }
    }

    // Try to extract a filename from a url, returns back the full url if unsuccessful
    private static string ExtractFileNameFromUrl(string url)
    {
        try
        {
            string[] splitString = url.Split('/');
            string lastSection = splitString[splitString.Length - 1];
            if (lastSection.Contains('?'))
            {
                lastSection = lastSection.Substring(0, lastSection.IndexOf('?'));
            }
            return lastSection;
        }
        catch (System.Exception)
        {
            return url;
        }
    }

    private static void ValidateHeightWidth(string dimensionString)
    {
        // if (dimensionString == null || dimensionString == "") return 0;

        // try
        // {
        //     int dimension = int.Parse(dimensionString);
        //     if (dimension < 20 || dimension > 4000) 
        //         throw new Exception();

        //     return dimension;
        // }
        // catch (System.Exception)
        // {

        //     return 0;
        // }
    }


    private static string CleanFileName(string fileName)
    {
        // If fileName contains a .extension other than webp, replace it with webp
        if (fileName.Contains('.') && !fileName.ToLower().EndsWith(".webp"))
        {
            string[] splitFileName = fileName.Split('.');
            string originalExtension = splitFileName[splitFileName.Length - 1].ToLower();
            fileName = fileName.Replace(originalExtension, "webp");
        }
        else
        {
            // If fileName has no .extension, add .webp
            fileName = fileName += ".webp";
        }
        return fileName;
    }

    private static async Task<Response> UploadStreamToS3(string fileName, Stream stream, ILogger log)
    {
        try
        {

            var putRequest = new PutObjectRequest
            {
                BucketName = s3bucket,
                Key = fileName,
                InputStream = stream
            };

            PutObjectResponse response = await s3client!.PutObjectAsync(putRequest);

            if (response.HttpStatusCode == System.Net.HttpStatusCode.OK)
            {
                // Clean up stream
                stream.Dispose();

                return new Response(true, "Uploaded to S3 successfully:\n\n" +
                $"https://{s3bucket}.s3.ap-southeast-2.amazonaws.com/{fileName}\n");
            }
            else
            {
                log.LogError(response.HttpStatusCode.ToString());
                return new Response(false, fileName + " was unable to be uploaded to S3");
            }
        }
        catch (AmazonS3Exception e)
        {
            log.LogError("S3 Exception: " + e.Message);
            return new Response(false, e.Message);
        }
        catch (System.Exception e)
        {
            log.LogError(e.Message);
            return new Response(false, e.Message);
        }
    }

    // Check if image already exists on S3, returns true if exists
    public static async Task<Response> ImageAlreadyExistsOnS3(string fileName, ILogger log)
    {
        try
        {
            var response = await s3client!.GetObjectAsync(bucketName: s3bucket, key: fileName);
            if (response.HttpStatusCode == HttpStatusCode.OK)
            {
                string msg = fileName + " already exists on S3 bucket";
                log.LogInformation(msg);
                return new Response(true, msg);
            }
            else
            {
                log.LogWarning("Received abnormal code from S3: " +
                response.HttpStatusCode.ToString());
                return new Response(false);
            }
        }
        catch (Amazon.S3.AmazonS3Exception e)
        {
            if (e.Message.StartsWith("The specified key does not exist."))
            {
                log.LogWarning("Key doesn't yet exist:" + fileName + "\n" + e.Message);
                return new Response(false);
            }
            else
            {
                log.LogError(e.Message);
                return new Response(true, e.ToString());
            }
        }
        catch (System.Exception e)
        {
            // Return true which will end the program and display the error
            log.LogError(e.Message);
            return new Response(true, e.ToString());
        }
    }

    // Takes a byte length such as 38043260 and returns a nicer string such as 38 MB
    public static string printFileSize(long byteLength)
    {
        if (byteLength < 1) return "0 KB";
        if (byteLength >= 1 && byteLength < 1000) return "1 KB";

        string longString = byteLength.ToString();
        if (byteLength >= 1000 && byteLength < 1000000)
            return longString.Substring(0, longString.Length - 3) + " KB";

        else return longString.Substring(0, longString.Length - 6) + " MB";
    }
}

// Response class used by functions that return to the main Run task
// Always return true/false whether the function succeeded, 
// and optionally provide a message and byte[] payload.
public class Response
{
    public bool Succeeded;
    public string Message;
    public byte[] bytePayload;

    public Response(bool Succeeded, string Message = "", byte[] bytePayload = null)
    {
        this.Succeeded = Succeeded;
        this.Message = Message;
        this.bytePayload = bytePayload;
    }
}

