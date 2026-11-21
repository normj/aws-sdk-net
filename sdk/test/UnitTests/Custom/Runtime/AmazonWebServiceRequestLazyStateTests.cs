/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 *
 *  http://aws.amazon.com/apache2.0
 *
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.UserAgent;
using Amazon.S3;
using Amazon.S3.Internal;
using Amazon.S3.Model;
using Amazon.S3.Model.Internal.MarshallTransformations;
using Amazon.Util;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// AmazonWebServiceRequest creates its user agent details and its event lock on first use, and the HTTP
    /// handler only merges request-level user agent details that exist. These tests pin what callers see.
    /// </summary>
    [TestClass]
    public class AmazonWebServiceRequestLazyStateTests
    {
        [TestMethod]
        public void UserAgentDetails_CreatedOnFirstAccessAndStable()
        {
            var request = new ListObjectsV2Request();
            Assert.IsNull(request.UserAgentDetailsIfCreated);

            var details = ((IAmazonWebServiceRequest)request).UserAgentDetails;
            Assert.IsNotNull(details);
            Assert.AreSame(details, ((IAmazonWebServiceRequest)request).UserAgentDetails);
            Assert.AreSame(details, request.UserAgentDetailsIfCreated);
            Assert.AreNotSame(details, ((IAmazonWebServiceRequest)new ListObjectsV2Request()).UserAgentDetails);
        }

        [TestMethod]
        public void UserAgentDetails_ConcurrentFirstAccessReturnsOneInstance()
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                var request = new ListObjectsV2Request();
                var seen = new UserAgentDetails[8];
                using (var start = new ManualResetEventSlim(false))
                {
                    var tasks = Enumerable.Range(0, seen.Length).Select(t => Task.Factory.StartNew(() =>
                    {
                        start.Wait();
                        seen[t] = ((IAmazonWebServiceRequest)request).UserAgentDetails;
                    }, TaskCreationOptions.LongRunning)).ToArray();
                    start.Set();
                    Task.WaitAll(tasks);
                }
                Assert.IsTrue(seen.All(d => ReferenceEquals(d, seen[0])));
            }
        }

        [TestMethod]
        public void BeforeRequestHandlers_AddRemoveAndFire()
        {
            var request = new ListObjectsV2Request();
            var iRequest = (IAmazonWebServiceRequest)request;
            int calls = 0;
            RequestEventHandler handler = (sender, args) => calls++;

            request.FireBeforeRequestEvent(this, null);
            Assert.AreEqual(0, calls);

            iRequest.AddBeforeRequestHandler(handler);
            iRequest.AddBeforeRequestHandler(handler);
            request.FireBeforeRequestEvent(this, null);
            Assert.AreEqual(2, calls);

            iRequest.RemoveBeforeRequestHandler(handler);
            request.FireBeforeRequestEvent(this, null);
            Assert.AreEqual(3, calls);

            iRequest.RemoveBeforeRequestHandler(handler);
            iRequest.RemoveBeforeRequestHandler(handler);
            request.FireBeforeRequestEvent(this, null);
            Assert.AreEqual(3, calls);
            Assert.IsNull(request.mBeforeRequestEvent);
        }

        [TestMethod]
        public void BeforeRequestHandlers_ConcurrentAddsAreNotLost()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var request = new ListObjectsV2Request();
                const int threads = 8, perThread = 200;
                int calls = 0;
                using (var start = new ManualResetEventSlim(false))
                {
                    var tasks = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
                    {
                        start.Wait();
                        for (int i = 0; i < perThread; i++)
                            ((IAmazonWebServiceRequest)request).AddBeforeRequestHandler((s, a) => Interlocked.Increment(ref calls));
                    }, TaskCreationOptions.LongRunning)).ToArray();
                    start.Set();
                    Task.WaitAll(tasks);
                }
                request.FireBeforeRequestEvent(this, null);
                Assert.AreEqual(threads * perThread, calls);
            }
        }

        [TestMethod]
        public void UserAgentHeader_SameWhetherOrNotTheRequestDetailsWereCreated()
        {
            var untouched = new PutObjectRequest { BucketName = "test", Key = "key", ContentBody = "body" };
            var touched = new PutObjectRequest { BucketName = "test", Key = "key", ContentBody = "body" };
            _ = ((IAmazonWebServiceRequest)touched).UserAgentDetails;

            var untouchedHeader = UserAgentHeader(untouched);
            var touchedHeader = UserAgentHeader(touched);

            Assert.IsNotNull(untouchedHeader);
            Assert.AreEqual(touchedHeader, untouchedHeader);
            Assert.IsNull(untouched.UserAgentDetailsIfCreated, "Sending a request does not create its user agent details");
        }

        [TestMethod]
        public void UserAgentHeader_IncludesRequestLevelComponentsAndFeatures()
        {
            var request = new PutObjectRequest { BucketName = "test", Key = "key", ContentBody = "body" };
            ((IAmazonWebServiceRequest)request).UserAgentDetails.AddUserAgentComponent("md/lazy-test");
            ((IAmazonWebServiceRequest)request).UserAgentDetails.AddFeature(UserAgentFeatureId.ACCOUNT_ID_MODE_DISABLED);

            var header = UserAgentHeader(request);

            StringAssert.Contains(header, "md/lazy-test");
            var metrics = header.Split(' ').First(part => part.StartsWith("m/", StringComparison.Ordinal)).Substring(2);
            StringAssert.Contains(metrics, UserAgentFeatureId.ACCOUNT_ID_MODE_DISABLED.Value);
        }

        private static string UserAgentHeader(AmazonWebServiceRequest request)
        {
            var config = new AmazonS3Config();
            var retryPolicy = new AmazonS3StandardRetryPolicy(config) { MaxBackoffInMilliseconds = 0 };
            var pipeline = new RuntimePipeline(new List<IPipelineHandler>
            {
                new HttpHandler<Stream>(new HttpHandlerTests.MockHttpRequestFactory(), new object()),
                // Points the request at the mock factory's canned PutObject response.
                new UserAgentTests.RequestInspectorHandler(),
                new Unmarshaller(true),
                new Signer(),
                new RetryHandler(retryPolicy),
                new ChecksumHandler(),
                new AmazonS3PostMarshallHandler(),
                new AmazonS3EndpointResolver(),
                new AmazonS3AuthSchemeHandler(),
                new Marshaller(),
                new AmazonS3PreMarshallHandler()
            });
            var context = new Amazon.Runtime.Internal.ExecutionContext(
                new RequestContext(true, new NullSigner())
                {
                    ClientConfig = config,
                    Marshaller = PutObjectRequestMarshaller.Instance,
                    OriginalRequest = request,
                    Request = PutObjectRequestMarshaller.Instance.Marshall(request),
                    Unmarshaller = PutObjectResponseUnmarshaller.Instance,
                    ExplicitAWSCredentials = new BasicAWSCredentials("awsAccessKeyId", "awsSecretAccessKey"),
                },
                new ResponseContext());
            pipeline.InvokeSync(context);
            context.RequestContext.Request.Headers.TryGetValue(HeaderKeys.UserAgentHeader, out var header);
            return header;
        }
    }
}
