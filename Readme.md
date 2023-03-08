# Image Transparent to S3 Func

This is an Azure Serverless Function that takes a url of an opaque image, converts it to a transparent webp image format, and then stores the result in AWS S3 bucket.

The function runs on Azure for its ease-of-use over Lambda, and images are stored on AWS S3 over Azure Storage for its better pricing structure.

The function is triggered with by a HTTP Trigger, and requires `id` and `url` query parameters to be set.
`id` represents the filename to be saved in S3. It shouldn't include a file extension.

Example:

```http
/HttpImageTrigger?id=12345&url=http://domain.com/filename.jpg
```

Azure Functions require an azure storage connection set.

Add image conversion stats.

Add more query parameters for resizing, quality.
