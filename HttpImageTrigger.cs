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

public static class HttpImageTrigger
{
    // Singletons for httpclient and s3
    static HttpClient httpclient;
    static IAmazonS3 s3client;
    static string s3bucket = "countdown-thumbs";

    [FunctionName("HttpImageTrigger")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
        ILogger log)
    {
        // Get query parameters from http trigger
        string id = req.Query["id"];
        string url = req.Query["url"];

        // If query parameters are not valid, return error Message
        if (id == null || url == null || id.Length < 3 || !url.Contains("http"))
        {
            return new OkObjectResult(
                "This function requires 'id' and 'url' query parameters\n " +
                "example: /HttpImageTrigger?id=12345&url=http://domain.com/filename.jpg"
            );
        }

        // Establish connection to S3
        var connectResponse = ConnectToS3();
        if (!connectResponse.Succeeded)
            return new BadRequestObjectResult(connectResponse.Message);

        // Init httpclient, s3client, out stream
        httpclient = new HttpClient();
        Stream outStream = new MemoryStream();

        // Derive filename to be written to S3 from id
        string fileName = id + ".webp";

        // Check S3 if file already exists
        var existsResponse = await imageAlreadyExistsOnS3(fileName);
        if (existsResponse.Succeeded) return new OkObjectResult(existsResponse.Message);

        // If it doesn't exist, download from url as bytes[]
        byte[] downloadedBytes;
        try
        {
            downloadedBytes = await httpclient!.GetByteArrayAsync(url);
            log.LogWarning(url);
        }
        catch (System.Exception e)
        {
            return new BadRequestObjectResult(url + " was unable to be downloaded" +
            e.Message + e.ToString());
        }

        string consolidatedMessage = "";

        // Load stream into ImageMagick, convert to transparent 180px webp image
        var imResponse = MakeImageTransparent(downloadedBytes, outStream, log);
        if (imResponse.Succeeded)
            consolidatedMessage += imResponse.Message + "\n";
        else
            return new BadRequestObjectResult(imResponse.Message);

        // Upload stream to S3
        var s3Response = await UploadStreamToS3(fileName, outStream, log);
        if (s3Response.Succeeded)
            return new OkObjectResult(consolidatedMessage + s3Response.Message);
        else
            return new BadRequestObjectResult(s3Response.Message);
    }

    private static Response ConnectToS3()
    {
        try
        {
            // Connect to S3 using credentials from env
            string accessKey = Environment.GetEnvironmentVariable("AWSAccessKey");
            string secretKey = Environment.GetEnvironmentVariable("AWSSecretKey");
            BasicAWSCredentials credentials = new BasicAWSCredentials(accessKey, secretKey);
            s3client = new AmazonS3Client(credentials, RegionEndpoint.APSoutheast2);

            return new Response(true, "");
        }
        catch (System.Exception)
        {
            return new Response(
                false,
                "Unable to connect to S3. Check credentials were set as environment settings:\n" +
                "\"AWSAccessKey\": \"<your aws access key>\",\n" +
                "\"AWSSecretKey\": \"<your aws secret key>\""
            );
        }
    }

    private static Response MakeImageTransparent(byte[] input, Stream outStream, ILogger log)
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

                // Sets the output format to 200x200 webp
                image.Scale(200, 200);
                image.Quality = 75;
                image.Format = MagickFormat.WebP;

                // Write the image to outStream
                image.Write(outStream);

                // If image conversion is successful, log Message
                long imageByteLength = outStream.Length;

                return new Response(
                    true,
                    "Image resized - original = " + printFileSize(input.LongLength) +
                    " - thumbnail = " + printFileSize(imageByteLength)
                );
            }
        }
        catch (System.Exception e)
        {
            log.LogError(e.ToString());
            return new Response(false, "Unable to be processed by ImageMagick");
        }
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

                return new Response(true, fileName + " uploaded to S3 successfully\n\n" +
                "https://urltogohere/" + s3bucket + "/" + fileName);
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
    public static async Task<Response> imageAlreadyExistsOnS3(string fileName)
    {
        try
        {
            var response = await s3client!.GetObjectAsync(bucketName: s3bucket, key: fileName);
            if (response.HttpStatusCode == HttpStatusCode.OK)
                return new Response(true, fileName + " already exists on S3 bucket");
            else
                return new Response(false, "");
        }
        catch (System.Exception e)
        {
            // If an exception occurs, return true which will end the program and display the error
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

public class Response
{
    public bool Succeeded;
    public string Message;
    public Response(bool Succeeded, string Message)
    {
        this.Succeeded = Succeeded;
        this.Message = Message;
    }
}

