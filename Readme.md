# Image Transparent to S3 Func

This is an Azure Serverless Function that takes a url of an opaque image, converts it into a transparent webp image format, and then stores the result in an AWS S3 bucket.

It runs using simple GET requests with exposed query parameters, and is served from `https://<func-app-name>.azurewebsites.net/api/ImageToS3?`

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

The function is triggered by http GET requests with query parameters. It requires `code`, `filename` and `url` query parameters to be set.

- `code` is the function level authorisation key, which can be obtained from the Azure Function > App keys section. This is not needed for local testing.
- `filename` represents the filename to be saved in S3. Any .extension will be replaced with the .webp format.
- `url` must be a valid image url.

Example Usage:

```html
https://<func-app>.azurewebsites.net/api/ImageToS3?code=<func-key>&filename=lightweight&url=http://domain.com/heavy-image.png
```

Output:

```cmd
Original Image: heavy-image.png
File Size: 532 KB

Successfully Converted to Transparent WebP

New Dimensions: 800x800
New File Size: 24 KB

Thumbnail Dimensions: 200x200
Thumbnail File Size: 6 KB

ImageMagick Variables Used:
Transparent Fuzz 3%
Quality 75%

Uploaded to S3:

https://<bucket>.s3.<region>.amazonaws.com/lightweight.webp
https://<bucket>.s3.<region>.amazonaws.com/full/lightweight.webp

```
