# Image Transparent to S3 Func

This is an Azure Serverless Function that takes urls of opaque images with white backgrounds, converts them into a transparent webp image format, and then stores the result in an AWS S3 bucket.

It runs using simple GET requests with exposed query parameters, and is served from:

`https://<func-app-name>.azurewebsites.net/api/ImageToS3?`

The function runs on a mix of Azure and AWS.
Azure Functions is used for its ease-of-use over Lambda, and images are stored on AWS S3 over Azure Storage for its better pricing structure.

## Setup

Requires an Azure Function with .NET 6 runtime.

For AWS, it is recommended to create a new user with minimum permission to read and write to S3.

The project can be tested locally. It will try to make use of any global AWS credentials and the Azure Storage Emulator.

Alternatively you can test with specific credentials by creating `local.settings.json` with the following:

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "<Your Azure Storage Connection String>",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet",
    "AWS_ACCESS_KEY": "<Your AWS Access Key>",
    "AWS_SECRET_KEY": "<Your AWS Secret Key>"
  }
}
```

If deploying to a hosted Azure Function, set the Application Settings for `AWS_ACCESS_KEY`, and `AWS_SECRET_KEY`.

## Usage

The function is triggered by http GET requests with query parameters. It requires `code`, `destination` and `source` query parameters to be set.

Optional `width` (default 200), `quality` (default 75), `fuzz` (default 3) parameters can also be set.

- `code` is the function level authorisation key, which can be obtained from the Azure Function > App keys section. This is not needed for local testing.
- `destination` represents the s3 path and filename to be saved to. Any .extension will be replaced with the .webp format.
- `source` must be a valid image url.

Example:

- `code=asdf1234==`
- `destination=s3://my-bucket/test/light-image.webp`
- `source=http://domain.com/heavy-image.png`

```html
ImageToS3?code=asdf1234==&filename=s3://my-bucket/test/light-image.webp&url=http://domain.com/heavy-image.png
```

## Output Response

```cmd
ImageToS3 v1.1 - powered by Azure Functions, AWS S3, and ImageMagick
--------------------------------------------------------------------

      Source: https://domain.com/img/heavy-image.png
 Destination: s3://my-bucket/test/light-image.webp
   Thumbnail: s3://my-bucket/test/200/light-image.webp

  Downloaded File In: 3.83s
    Source File Size: 136 KB
   Source Dimensions: 1920 x 1080

ImageMagick Conversion - Took 0.96s
------------------------------------
       New WebP Size: 37 KB
        WebP Quality: 75%
   Transparency Fuzz: 3%

Thumbnail Dimensions: 200 x 200
 Thumbnail File Size: 3 KB

S3 Upload of Full-Size and Thumbnail WebPs:
-------------------------------------------
https://my-bucket.s3.my-region.amazonaws.com/test/light-image.webp
https://my-bucket.s3.my-region.amazonaws.com/test/200/light-image.webp

S3 Upload Took 0.4s
```

## Output Images

![alt text](https://github.com/Jason-nzd/ImageToS3Func/raw/master/image-comparison-1000.jpg?raw=true "Image Comparison")
