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

        // Check query parameters are valid
        if (id == null || url == null || id.Length < 3 || !url.Contains("http"))
            return new OkObjectResult(
                "This function requires 'id' and 'url' query parameters\n " +
                "example: /HttpImageTrigger?id=12345&url=http://domain.com/filename.jpg"
            );

        // Derive filename to be written to S3 from id
        string fileName = id + ".webp";

        // Init httpclient, s3client, out stream
        httpclient = new HttpClient();
        s3client = new AmazonS3Client(RegionEndpoint.APSoutheast2);
        Stream outStream = new MemoryStream();

        // Check S3 if file already exists
        if (await imageAlreadyExistsOnS3(fileName))
            return new OkObjectResult(fileName + " already exists on S3 bucket");

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

        // Load stream into ImageMagick, convert to transparent 180px webp image
        bool imResponse = MakeImageTransparent(downloadedBytes, outStream, log);
        if (!imResponse)
            return new BadRequestObjectResult(fileName + " was unable to be processed by ImageMagick");

        // Upload stream to S3
        bool s3Response = await UploadStreamToS3(fileName, outStream, log);
        if (s3Response)
            return new OkObjectResult(fileName + " uploaded to S3 successfully");
        else
            return new BadRequestObjectResult(fileName + " was unable to be downloaded");
    }

    private static bool MakeImageTransparent(byte[] input, Stream outStream, ILogger log)
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

                // Sets the output format to 180x180 webp
                image.Scale(180, 180);
                image.Quality = 60;
                image.Format = MagickFormat.WebP;

                // Write the image to outStream
                image.Write(outStream);

                // If image conversion is successful, log message
                long imageByteLength = outStream.Length;
                log.LogWarning(
                    "Image resized - original = " + printFileSize(input.LongLength) +
                    " - thumbnail = " + printFileSize(imageByteLength)
                );

                return true;
            }
        }
        catch (System.Exception e)
        {
            log.LogError(e.ToString());
            return false;
        }
    }

    private static async Task<bool> UploadStreamToS3(string fileName, Stream stream, ILogger log)
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
                // Clean up streams
                stream.Dispose();

                return true;
            }
            else
            {
                log.LogError(response.HttpStatusCode.ToString());
                return false;
            }
        }
        catch (AmazonS3Exception e)
        {
            log.LogError("S3 Exception: " + e.Message);
            return false;
        }
        catch (System.Exception e)
        {
            log.LogError(e.Message);
            return false;
        }
    }

    // Check if image already exists on S3, returns true if exists
    public static async Task<bool> imageAlreadyExistsOnS3(string fileName)
    {
        try
        {
            var response = await s3client!.GetObjectAsync(bucketName: s3bucket, key: fileName);
            if (response.HttpStatusCode == HttpStatusCode.OK)
                return true;
            else
                return false;
        }
        catch (System.Exception)
        {
            return false;
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

