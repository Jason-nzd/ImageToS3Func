# Image Transparent to S3 Func

This is an Azure Serverless Function that takes a url of an opaque image, converts it into a transparent webp image format, and then stores the result in an AWS S3 bucket.

The function runs on a mix of Azure and AWS.
Azure Functions is used for its ease-of-use over Lambda, and images are stored on AWS S3 over Azure Storage for its better pricing structure.

## Setup

Requires an Azure Function with .NET 6 runtime.

For AWS, it is recommended to create a new user with minimum permission to read and write to S3.

The project can be tested locally. It will try to make use of any global AWS credentials, and the built-in azure storage emulator.

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

To deploy to an Azure Function, set the Application Settings for `AWS_ACCESS_KEY`, and `AWS_SECRET_KEY`.

## Usage

The function is triggered by http GET request. It requires `code`, `filename` and `url` query parameters to be set.

- `code` is the function level authorisation key, which can be obtained from the Azure Function > App keys section.
- `filename` represents the filename to be saved in S3. Any .extension will be replaced with the .webp format.
- `url` must be a valid image url.

Example Usage:

```html
https://<func-app-name>.azurewebsites.net/api/ImageTransparent?code=asdf1234==&filename=lightweight-image&url=http://domain.com/heavy-image.png
```

Output:

```cmd
Original Image: heavy-image.png
Size: 521 KB

Converted to Transparent Image with Dimensions: 200x200 
Size: 9 KB

Uploaded to S3 as:

https://bucket.s3.region.amazonaws.com/lightweight-image.webp
```
