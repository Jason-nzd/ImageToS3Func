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

public static class ImageToS3Func
{
    // Singletons and variables for S3 and logging
    static readonly RegionEndpoint region = RegionEndpoint.APSoutheast2;
    static IAmazonS3 s3client;
    static string s3bucket = "", s3path = "";
    static int thumbWidth, quality, fuzz, maximumDesiredHeight;
    static DateTime startTime;
    static HttpClient httpClient = new HttpClient();
    static bool rejectGreyscale = true;

    [FunctionName("ImageToS3")]
    public static async Task<IActionResult> Run(

    // Trigger off http GET requests
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
    ILogger log)
    {
        // Build a consolidated message string, which will be added to by other functions
        string consolidatedMsg = "ImageToS3 v1.5.2 - powered by Azure Functions, AWS S3, and ImageMagick\n";
        consolidatedMsg += "".PadRight(70, '-') + "\n\n";

        // Store start time for logging function duration
        startTime = DateTime.Now;

        // Get query parameters from http trigger
        string destination = req.Query["destination"];
        string source = req.Query["source"];

        // Check for overwrite of existing images
        string overwrite = req.Query["overwrite"];
        bool forceOverwrite = (overwrite != null && overwrite == "true");

        // Check for reject greyscale flag, which defaults to true
        if (req.Query["rejectGreyscale"] == false) rejectGreyscale = false;

        // To prevent excess PUT requests, checks can be made for already existing image on the S3 Bucket,
        //  or if a CDN is in use, the CDN can be checked first to reduce S3 GET requests.
        string cdnPath = req.Query["cdnPath"];
        bool checkCDNForExistingImagesFirst = (cdnPath != null);

        try
        {
            // Try parse integers from query parameters
            // If query params don't exist or are invalid, the default value will be used
            thumbWidth = ParseIntWithRange(req.Query["width"], min: 16, max: 512, defaultValue: 200);
            quality = ParseIntWithRange(req.Query["quality"], min: 5, max: 100, defaultValue: 70);
            fuzz = ParseIntWithRange(req.Query["fuzz"], min: 0, max: 100, defaultValue: 3);
            maximumDesiredHeight = ParseIntWithRange(
                req.Query["maximumDesiredHeight"],
                min: 16,
                max: 16000,
                defaultValue: 1024
            );


            // Validate url query parameters, return if unable to validate inputs
            var inputResponse = ValidateURLParameters(destination, source);
            consolidatedMsg += inputResponse.Message;
            if (!inputResponse.Succeeded)
                return new BadRequestObjectResult(consolidatedMsg);
            string fileName = ValidateFileName(destination);


            // Establish connection to S3, return if unable to connect
            var connectResponse = await ConnectToS3();
            if (!connectResponse.Succeeded)
                return new BadRequestObjectResult(consolidatedMsg + connectResponse.Message);


            if (!forceOverwrite)
            {
                // Conditionally check CDN if file already exists, return if already exists,
                //  this saves on more expensive GET requests to S3
                if (checkCDNForExistingImagesFirst)
                {
                    Response cdnResponse = await ImageAlreadyExistsOnCDN(fileName, cdnPath, log);
                    consolidatedMsg += cdnResponse.Message;
                    if (cdnResponse.Succeeded) return new OkObjectResult(consolidatedMsg);
                }

                // Check S3 if file already exists, return if already exists
                Response existsResponse = await ImageAlreadyExistsOnS3(fileName, log);
                consolidatedMsg += existsResponse.Message;
                if (existsResponse.Succeeded)
                    return new OkObjectResult(consolidatedMsg);
            }

            // Download from url, return if unsuccessful
            var downloadResponse = await DownloadImageUrlToStream(source, log);
            consolidatedMsg += downloadResponse.Message;
            if (!downloadResponse.Succeeded)
                return new BadRequestObjectResult(consolidatedMsg);


            // Load stream into ImageMagick for conversion, return if unsuccessful
            Stream thumbnailImageStream = new MemoryStream();
            Stream fullSizeImageStream = new MemoryStream();

            // Process image streams into transparent and compressed formats
            var imResponse = MakeImageTransparent(
                downloadResponse.bytePayload,
                fullSizeImageStream,
                thumbnailImageStream,
                log,
                quality,
                fuzz
            );
            consolidatedMsg += imResponse.Message;
            if (!imResponse.Succeeded)
                return new BadRequestObjectResult(consolidatedMsg);


            // Upload full size image stream to S3, return if failed
            consolidatedMsg += "S3 Upload of Full-Size and Thumbnail WebPs:\n" + "".PadRight(43, '-') + "\n";

            var fullSizeResponse = await UploadStreamToS3(fileName, fullSizeImageStream, log);
            consolidatedMsg += fullSizeResponse.Message;
            if (!fullSizeResponse.Succeeded) return new BadRequestObjectResult(consolidatedMsg);


            // Upload thumbnail stream to S3, return if unsuccessful
            var thumbnailResponse = await UploadStreamToS3(fileName,
                thumbnailImageStream, log, addThumbPath: thumbWidth.ToString() + "/"
            );
            consolidatedMsg += thumbnailResponse.Message;
            if (!thumbnailResponse.Succeeded)
                return new BadRequestObjectResult(consolidatedMsg);


            // Log CDN urls of full-size and thumbnail if applicable
            if (checkCDNForExistingImagesFirst)
            {
                string cdnDomain = Environment.GetEnvironmentVariable("CDN_DOMAIN");
                consolidatedMsg += $"{cdnDomain}/{s3path}{thumbWidth}/{fileName}\n";
                consolidatedMsg += $"{cdnDomain}/{s3path}{fileName}\n";
            }

            // Log S3 upload time
            double timeElapsed = Math.Round((DateTime.Now - startTime).TotalSeconds, 2);
            consolidatedMsg += $"\nS3 Upload Took {timeElapsed}s";


            // Return final consolidated msg
            return new OkObjectResult(consolidatedMsg);

        }
        catch (Exception e)
        {
            return new BadRequestObjectResult(consolidatedMsg + "\nError: " + e);
        }
    }

    private static Response ValidateURLParameters(string destination, string source)
    {
        // If source or destination query parameters are not valid, return error Message
        if (destination == null || source == null || !destination.Contains("s3://") || !source.Contains("http"))
        {
            return new Response(false,
                "This function requires 'destination=' and 'source=' query parameters \nExample:\n\n" +
                "/ImageToS3?destination=s3://mybucket/path/new-file&source=https://domain.com/heavy-image.png\n\n" +
                "Optional parameters:\n" +
                "'width=' (default 200) (thumbnail width)\n" +
                "'quality=' (default 70) (image compression quality 1-100)\n" +
                "'fuzz=' (default 3) (transparency detection fuzz threshold 1-50)\n" +
                "'overwrite=' (default false) (overwrites images that already exist)\n" +
                "'cdnPath=' (default none) (checks CDN before S3 to see if image already exists)\n" +
                "'rejectGreyscale=false' (default true) (rejects source images which are greyscale)\n"
            );
        }

        // Split s3 destination string into array of strings
        // Example: s3://mybucket/optional/path/filetosave.webp
        // Results: [s3:, , mybucket, optional, path, filetosave.webp]
        string[] destinationChunks = destination.Split('/');

        // Get bucket name from the 2nd index
        s3bucket = destinationChunks[2];

        // Get optional path string from 3rd to the 2nd to last index
        s3path = "";
        if (destinationChunks.Length > 4)
        {
            for (int i = 3; i < destinationChunks.Length - 1; i++)
            {
                s3path += destinationChunks[i] + "/";
            }
        }

        // Get filename from last index
        string fileName = destinationChunks[destinationChunks.Length - 1];

        // Build return msg
        string successMsg = $"      Source: {source}\n" +
            $" Destination: s3://{s3bucket}/{s3path}{fileName}\n" +
            $"   Thumbnail: s3://{s3bucket}/{s3path}{thumbWidth}/{fileName}\n";

        // Add info to consolidatedMsg
        return new Response(true, successMsg + "\n");
    }

    private static string ValidateFileName(string destination)
    {
        // Split s3 destination string into array of strings
        // Example: s3://mybucket/optional/path/filetosave.webp
        // Results: [s3:, , mybucket, optional, path, filetosave.webp]
        string[] destinationChunks = destination.Split('/');

        // Get filename from last index
        string fileName = destinationChunks[destinationChunks.Length - 1];

        // If filename contains a .extension, replace it with webp
        if (fileName.Contains('.'))
        {
            string[] splitfileName = fileName.Split('.');
            string originalExtension = splitfileName[splitfileName.Length - 1].ToLower();
            fileName = fileName.Replace(originalExtension, "webp");
        }
        else
        {
            // If fileName has no .extension, add .webp
            fileName = fileName += ".webp";
        }
        return fileName;
    }

    private static async Task<Response> DownloadImageUrlToStream(string url, ILogger log)
    {
        try
        {
            // Download image using httpclient
            byte[] downloadedBytes = await httpClient!.GetByteArrayAsync(url);

            // Log filename
            log.LogWarning("Downloaded " + ExtractFileNameFromUrl(url));

            // Measure time elapsed
            double timeElapsed = Math.Round((DateTime.Now - startTime).TotalSeconds, 2);
            startTime = DateTime.Now;   // reset startTime for later section time measures

            // Return Response with success, message, and byte[] payload
            return new Response(
                true,
                $"\nDownloading File Took: {timeElapsed}s\n" +
                $"     Source File Size: {printFileSize(downloadedBytes.LongLength)}\n",
                downloadedBytes
            );
        }
        catch (Exception e)
        {
            return new Response(
                false,
                "Error: Unable to download:\n\n" + url + "\n\n" + e.Message
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
        catch (Exception e)
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
                "Error: Unable to connect to S3. \n\n" +
                "Check Azure Function Application Settings or local.settings.json were set:\n" +
                "AWS_ACCESS_KEY: <your aws access key>\n" +
                "AWS_SECRET_KEY: <your aws secret key>\n\n" + e.Message
            );
        }
    }

    private static Response MakeImageTransparent(
        byte[] input,
        Stream fullSizeImageStream,
        Stream thumbnailImageStream,
        ILogger log,
        int quality,
        int fuzz
    )
    {
        try
        {
            using (var image = new MagickImage(input))
            {
                // Store original source image dimensions
                int originalWidth = image.Width;
                int originalHeight = image.Height;

                // Check if the image is greyscale by comparing it to a desaturated copy
                if (rejectGreyscale)
                {
                    MagickImage desaturatedCopy = new MagickImage(image);
                    desaturatedCopy.Modulate(new Percentage(100), new Percentage(0));

                    // Reject greyscale images which have an error amount < 0.001
                    double comparisonErrorAmount = image.Compare(desaturatedCopy, ErrorMetric.RootMeanSquared);
                    if (comparisonErrorAmount < 0.001)
                    {
                        return new Response(
                            false,
                            $"\nSource image rejected as greyscale with error amount: {comparisonErrorAmount}"
                        );
                    }
                }

                // Converts white pixels into transparent pixels with a default fuzz of 3
                image.ColorFuzz = new Percentage(fuzz);
                image.Alpha(AlphaOption.Set);
                image.BorderColor = MagickColors.White;
                image.Border(1);
                image.Settings.FillColor = MagickColors.Transparent;
                image.FloodFill(MagickColors.Transparent, 1, 1);
                image.Shave(1, 1);

                // Trim excess whitespace
                image.Trim();

                // Resize to a maximum height if needed
                int resizedWidth = 0;
                int resizedHeight = 0;

                if (image.Height > maximumDesiredHeight)
                {
                    Console.WriteLine($"Image height of {image.Height} will be downsized to {maximumDesiredHeight}");

                    // Calculate the percentage to scale down to reach the desired maximum height
                    int heightDifference = Math.Abs(maximumDesiredHeight - image.Height);
                    float percentDifference = heightDifference / (float)image.Height * 100f;

                    // Perform the resize and store the new dimensions for logging purposes
                    image.Resize(new Percentage(100f - percentDifference));
                    resizedWidth = image.Width;
                    resizedHeight = image.Height;
                }

                // Output full image to WebP format
                image.Quality = quality;
                image.Format = MagickFormat.WebP;
                image.Write(fullSizeImageStream);

                // Scale down and output thumbnail image to a fixed square
                image.BackgroundColor = new MagickColor(0, 0, 0, 0);
                MagickGeometry square = new MagickGeometry(thumbWidth, thumbWidth);
                image.Resize(square);
                image.Extent(square, Gravity.Center);

                // Write to Stream
                image.Write(thumbnailImageStream);

                // Measure time elapsed
                double timeElapsed = Math.Round((DateTime.Now - startTime).TotalSeconds, 2);
                startTime = DateTime.Now;   // reset startTime for later section time measures

                // If image conversion is successful, log Message
                return new Response(
                    true,
                    $"    Source Dimensions: {originalWidth} x {originalHeight}\n\n" +

                    $"ImageMagick Conversion Took: {timeElapsed}s\n" + "".PadRight(34, '-') + "\n" +
                    $"        New File Size: {printFileSize(fullSizeImageStream.Length)}\n" +
                    $"         WebP Quality: {quality}%\n" +
                    $"    Transparency Fuzz: {fuzz}%\n" +

                    // Conditionally log the resized dimensions only if a resize was performed
                    ((resizedWidth != 0) ?
                        $"   Resized Dimensions: {resizedWidth} x {resizedHeight}\n\n"
                        : "\n") +

                    $" Thumbnail Dimensions: {thumbWidth} x {thumbWidth}\n" +
                    $"  Thumbnail File Size: {printFileSize(thumbnailImageStream.Length)}\n\n"
                );
            }
        }
        catch (Exception e)
        {
            log.LogError(e.Message);
            return new Response(false, "\nImage unable to be processed by ImageMagick - Check it is a valid image url");
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
        catch (Exception)
        {
            return url;
        }
    }

    // Parses a string of a number that should be within min - max range
    //  returns -1 if unsuccessful
    private static int ParseIntWithRange(string numString, int min, int max, int defaultValue)
    {
        try
        {
            int num = int.Parse(numString);
            if (num < min || num > max)
                throw new Exception();
            return num;
        }
        catch (Exception)
        {
            return defaultValue;
        }
    }

    private static async Task<Response> UploadStreamToS3(
        string fileName,
        Stream stream,
        ILogger log,
        string addThumbPath = "")
    {
        try
        {
            string fileKey = s3path + addThumbPath + fileName;
            var putRequest = new PutObjectRequest
            {
                BucketName = s3bucket,
                Key = fileKey,
                InputStream = stream
            };

            PutObjectResponse response = await s3client!.PutObjectAsync(putRequest);

            if (response.HttpStatusCode == System.Net.HttpStatusCode.OK)
            {
                // Clean up stream
                stream.Dispose();

                return new Response(
                    true,
                    $"https://{s3bucket}.s3.ap-southeast-2.amazonaws.com/{fileKey}\n"
                );
            }
            else
            {
                log.LogError(response.HttpStatusCode.ToString());
                return new Response(false, fileKey + " was unable to be uploaded to S3");
            }
        }
        catch (AmazonS3Exception e)
        {
            log.LogError("S3 Exception: " + e.Message);
            return new Response(false, e.Message);
        }
        catch (Exception e)
        {
            log.LogError(e.Message);
            return new Response(false, e.Message);
        }
    }

    // Check if image already exists on S3, returns true if exists
    public static async Task<Response> ImageAlreadyExistsOnS3(string fileName, ILogger log)
    {
        string fileKey = s3path + fileName;
        try
        {
            var response = await s3client!.GetObjectAsync(bucketName: s3bucket, key: fileKey);
            if (response.HttpStatusCode == HttpStatusCode.OK)
            {
                string msg = $"s3://{s3bucket}/{fileKey} already exists on S3 bucket";
                log.LogInformation(msg);
                return new Response(true, msg);
            }
            else
            {
                string msg = "Received abnormal code from S3: " + response.HttpStatusCode.ToString();
                log.LogWarning(msg);
                return new Response(true, msg);
            }
        }
        catch (Amazon.S3.AmazonS3Exception e)
        {
            if (e.Message.StartsWith("The specified key does not exist."))
            {
                return new Response(false, $"s3://{s3bucket}/{fileKey} doesn't yet exist on S3\n");
            }
            else
            {
                log.LogError(e.Message);
                return new Response(true, e.ToString());
            }
        }
        catch (Exception e)
        {
            // Return true which will end the program and display the error
            log.LogError(e.Message);
            return new Response(true, e.ToString());
        }
    }

    // Checks if file already exists on CDN
    public static async Task<Response> ImageAlreadyExistsOnCDN(string fileName, string cdnPath, ILogger log)
    {
        try
        {
            string fileKey = s3path + fileName;
            if (cdnPath.EndsWith("/")) cdnPath = cdnPath.Substring(0, cdnPath.Length - 1);

            var response = await httpClient.GetAsync(cdnPath + "/" + fileKey);

            // If existing image found on CDN, we can end the program
            if (response.StatusCode == HttpStatusCode.OK)
            {
                string msg = $"{cdnPath}/{fileKey} already exists on CDN\n";
                log.LogInformation(msg);
                return new Response(true, msg);
            }
            else if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.NotFound)
            {
                string msg = $"{cdnPath}/{fileKey} not found on CDN\n";
                return new Response(false, msg);
            }
            else
            {
                string msg = $"ImageAlreadyExistsOnCDN() - Received abnormal code: {response.StatusCode}\n";
                log.LogWarning(msg);
                return new Response(true, msg);
            }
        }
        catch (Exception e)
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

