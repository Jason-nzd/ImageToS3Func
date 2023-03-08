# Image Transparent to S3 Func

This is an Azure Serverless Function that takes a url of an opaque image, converts it to a transparent webp image format, and then stores the result in an AWS S3 bucket.

The function runs on a mix of Azure and AWS.
Azure Functions is used for its ease-of-use over Lambda, and images are stored on AWS S3 over Azure Storage for its better pricing structure.

## Setup

Requires an Azure Storage Account used for temporary storage to run the Azure Function.

For AWS, it is recommended to create a new user with minimum permission to read and write to S3.

To run locally, download project and create a `local.settings.json` with the following:  

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

To deploy to an Azure Function, set the Application Settings for `AzureWebJobsStorage`, `AWS_ACCESS_KEY`, and `AWS_SECRET_KEY`.

## Usage

The function is triggered by http GET request. It requires `fileName` and `url` query parameters to be set.
`fileName` represents the filename to be saved in S3, and any .extension will be replaced with .webp format.

Example Usage:

```html
/HttpImageTrigger?id=12345&url=http://domain.com/filename.png
```

Output:

```cmd
12345.webp
todo with sample images
```
