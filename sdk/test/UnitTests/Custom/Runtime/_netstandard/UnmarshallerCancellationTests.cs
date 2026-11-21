using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AWSSDK_DotNet.CommonTest.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Regression test for https://github.com/aws/aws-sdk-net/issues/4534: once response headers are received,
    /// the cancellation token must still abort a body read that never completes.
    /// </summary>
    [TestClass]
    public class UnmarshallerCancellationTests
    {
        /// <summary>
        /// Sends the given status with the start of a ListBucketResult body, then holds the connection open without
        /// sending the rest until released.
        /// </summary>
        private class StalledBodyServlet : Servlet
        {
            private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);
            private readonly int _statusCode;

            public StalledBodyServlet(int statusCode)
            {
                _statusCode = statusCode;
            }

            public void Release()
            {
                _release.Set();
            }

            protected override void HandleRequest(HttpListenerContext context)
            {
                var partialBody = Encoding.UTF8.GetBytes(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                    "<Name>bucket</Name><Contents><Key>a</Key>");

                context.Response.StatusCode = _statusCode;
                context.Response.ContentType = "application/xml";
                context.Response.SendChunked = true;
                context.Response.OutputStream.Write(partialBody, 0, partialBody.Length);
                context.Response.OutputStream.Flush();
                _release.Wait(TimeSpan.FromSeconds(10));
            }

            protected override void Cleanup()
            {
                _release.Dispose();
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        public async Task CancellationIsHonoredWhenResponseBodyStalls()
        {
            await AssertCancelledAsync(200);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        public async Task CancellationIsHonoredWhenErrorResponseBodyStalls()
        {
            await AssertCancelledAsync(500);
        }

        /// <summary>
        /// Sends a complete response with the given status and body.
        /// </summary>
        private class CompleteBodyServlet : Servlet
        {
            private readonly int _statusCode;
            private readonly string _body;

            public CompleteBodyServlet(int statusCode, string body)
            {
                _statusCode = statusCode;
                _body = body;
            }

            protected override void HandleRequest(HttpListenerContext context)
            {
                context.Response.ContentType = "application/xml";
                WriteResponse(context.Response, _body, _statusCode);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        public async Task UncancelableTokenReadsCompleteResponseBody()
        {
            var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                "<Name>bucket</Name><Contents><Key>a</Key></Contents><Contents><Key>b</Key></Contents></ListBucketResult>";

            using (var servlet = new CompleteBodyServlet(200, body))
            using (var client = CreateClient(servlet))
            {
                foreach (var token in new[] { CancellationToken.None, default(CancellationToken), new CancellationToken(false) })
                {
                    Assert.IsFalse(token.CanBeCanceled);
                    var response = await client.ListObjectsAsync(new ListObjectsRequest { BucketName = "bucket" }, token);
                    Assert.AreEqual("bucket", response.Name);
                    Assert.AreEqual(2, response.S3Objects.Count);
                    Assert.AreEqual("b", response.S3Objects[1].Key);
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        public async Task UncancelableTokenReadsCompleteErrorResponseBody()
        {
            var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<Error><Code>AccessDenied</Code><Message>Access Denied</Message><RequestId>REQ123</RequestId></Error>";

            using (var servlet = new CompleteBodyServlet(403, body))
            using (var client = CreateClient(servlet))
            {
                var ex = await Assert.ThrowsExactlyAsync<AmazonS3Exception>(
                    () => client.ListObjectsAsync(new ListObjectsRequest { BucketName = "bucket" }, CancellationToken.None));
                Assert.AreEqual("AccessDenied", ex.ErrorCode);
                Assert.AreEqual("Access Denied", ex.Message);
                Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        public async Task CancelableTokenThatIsNotCanceledReadsCompleteResponseBody()
        {
            var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                "<Name>bucket</Name><Contents><Key>a</Key></Contents></ListBucketResult>";

            using (var servlet = new CompleteBodyServlet(200, body))
            using (var client = CreateClient(servlet))
            using (var cts = new CancellationTokenSource())
            {
                var response = await client.ListObjectsAsync(new ListObjectsRequest { BucketName = "bucket" }, cts.Token);
                Assert.AreEqual(1, response.S3Objects.Count);
                // Cancelling after the call finished must not throw: the registration was disposed with the call.
                cts.Cancel();
            }
        }

        private static AmazonS3Client CreateClient(Servlet servlet)
        {
            var config = new AmazonS3Config
            {
                ServiceURL = servlet.ServiceURL,
                ForcePathStyle = true,
                MaxErrorRetry = 0
            };
            return new AmazonS3Client(new BasicAWSCredentials("ACCESS", "SECRET"), config);
        }

        private static async Task AssertCancelledAsync(int statusCode)
        {
            using (var servlet = new StalledBodyServlet(statusCode))
            {
                var config = new AmazonS3Config
                {
                    ServiceURL = servlet.ServiceURL,
                    ForcePathStyle = true,
                    MaxErrorRetry = 0
                };

                using (var client = new AmazonS3Client(new BasicAWSCredentials("ACCESS", "SECRET"), config))
                using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
                {
                    var listTask = client.ListObjectsAsync(new ListObjectsRequest { BucketName = "bucket" }, cts.Token);
                    var completed = await Task.WhenAny(listTask, Task.Delay(TimeSpan.FromSeconds(5)));
                    servlet.Release();

                    if (completed != listTask)
                    {
                        Assert.Fail("ListObjectsAsync did not complete after the cancellation token was cancelled.");
                    }
                    await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => listTask);
                }
            }
        }
    }
}
