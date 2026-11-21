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
using System.Linq;
using System.Net;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.Util;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// On .NET 8+ the retry handler grows the request's header dictionary once, before adding the retry
    /// headers. These tests pin what callers can observe: the same dictionary instance, the same headers
    /// in the same order, and the same retry header values on every attempt.
    /// </summary>
    [TestClass]
    public class RetryHandlerHeaderCapacityTests
    {
        private sealed class TestRequest : AmazonWebServiceRequest { }

        private sealed class RecordingHandler : PipelineHandler
        {
            public int Failures { get; set; }
            public List<List<KeyValuePair<string, string>>> Seen { get; } = new List<List<KeyValuePair<string, string>>>();
            public List<IDictionary<string, string>> Instances { get; } = new List<IDictionary<string, string>>();

            public override void InvokeSync(IExecutionContext executionContext)
            {
                var headers = executionContext.RequestContext.Request.Headers;
                Instances.Add(headers);
                Seen.Add(headers.ToList());
                if (Seen.Count <= Failures)
                    throw new AmazonServiceException("Mocked service error", new WebException(), HttpStatusCode.InternalServerError);
                // Later handlers (signer, HTTP handler) add headers on every attempt.
                headers["X-Amz-Date"] = "20261121T060000Z";
                headers["Authorization"] = "AWS4-HMAC-SHA256 attempt " + Seen.Count;
            }
        }

        private static (RecordingHandler recorder, IExecutionContext context) Run(int marshalledHeaders, int failures, bool presetInvocationId = false)
        {
            var config = new AmazonS3Config
            {
                ServiceURL = $"https://s3-{Guid.NewGuid()}.amazonaws.com",
                RetryMode = RequestRetryMode.Standard,
            };
            var recorder = new RecordingHandler { Failures = failures };
            var pipeline = new RuntimePipeline(recorder);
            pipeline.AddHandler(new RetryHandler(new MockStandardRetryPolicy(config)));

            var request = new DefaultRequest(new TestRequest(), "TestService") { Endpoint = new Uri(config.ServiceURL) };
            for (int i = 0; i < marshalledHeaders; i++)
                request.Headers["X-Marshalled-" + i] = i.ToString();
            if (presetInvocationId)
                request.Headers[HeaderKeys.AmzSdkInvocationId] = "caller-supplied";

            var requestContext = new RequestContext(false)
            {
                ClientConfig = config,
                OriginalRequest = new TestRequest(),
                Request = request,
            };
            var context = new ExecutionContext(requestContext, new ResponseContext());
            pipeline.InvokeSync(context);
            return (recorder, context);
        }

        [TestMethod]
        [DataRow(0)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(9)]
        [DataRow(17)]
        [DataRow(40)]
        public void FirstAttempt_HeadersAreMarshalledOnesThenRetryHeadersInOrder(int marshalledHeaders)
        {
            var (recorder, context) = Run(marshalledHeaders, failures: 0);

            var expected = Enumerable.Range(0, marshalledHeaders)
                .Select(i => new KeyValuePair<string, string>("X-Marshalled-" + i, i.ToString()))
                .Concat(new[]
                {
                    new KeyValuePair<string, string>(HeaderKeys.AmzSdkInvocationId, context.RequestContext.InvocationId.ToString()),
                    new KeyValuePair<string, string>(HeaderKeys.AmzSdkRequest, "attempt=1; max=3"),
                })
                .ToList();
            CollectionAssert.AreEqual(expected, recorder.Seen.Single());
            Assert.AreSame(context.RequestContext.Request.Headers, recorder.Instances.Single());
        }

        [TestMethod]
        [DataRow(2)]
        [DataRow(9)]
        public void Retries_KeepTheInvocationIdAndUpdateTheAttemptInPlace(int marshalledHeaders)
        {
            var (recorder, context) = Run(marshalledHeaders, failures: 2);

            Assert.AreEqual(3, recorder.Seen.Count);
            var invocationId = context.RequestContext.InvocationId.ToString();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var headers = recorder.Seen[attempt];
                var names = headers.Select(h => h.Key).ToList();
                var expectedNames = Enumerable.Range(0, marshalledHeaders).Select(i => "X-Marshalled-" + i)
                    .Concat(new[] { HeaderKeys.AmzSdkInvocationId, HeaderKeys.AmzSdkRequest })
                    .ToList();
                CollectionAssert.AreEqual(expectedNames, names);
                Assert.AreEqual(invocationId, headers.Single(h => h.Key == HeaderKeys.AmzSdkInvocationId).Value);
                Assert.AreEqual($"attempt={attempt + 1}; max=3", headers.Single(h => h.Key == HeaderKeys.AmzSdkRequest).Value);
                Assert.AreSame(context.RequestContext.Request.Headers, recorder.Instances[attempt]);
            }

            var final = context.RequestContext.Request.Headers;
            Assert.AreEqual("AWS4-HMAC-SHA256 attempt 3", final["authorization"]);
            Assert.AreEqual(marshalledHeaders + 4, final.Count);
        }

        [TestMethod]
        public void CallerSuppliedInvocationId_IsKept()
        {
            var (recorder, _) = Run(2, failures: 0, presetInvocationId: true);
            var headers = recorder.Seen.Single();
            Assert.AreEqual("caller-supplied", headers.Single(h => h.Key == HeaderKeys.AmzSdkInvocationId).Value);
            CollectionAssert.AreEqual(
                new[] { "X-Marshalled-0", "X-Marshalled-1", HeaderKeys.AmzSdkInvocationId, HeaderKeys.AmzSdkRequest },
                headers.Select(h => h.Key).ToArray());
        }

        [TestMethod]
        public void HeadersStillIgnoreCaseAfterGrowing()
        {
            var (_, context) = Run(2, failures: 0);
            var headers = context.RequestContext.Request.Headers;
            Assert.IsTrue(headers.ContainsKey("X-MARSHALLED-1"));
            Assert.IsTrue(headers.ContainsKey(HeaderKeys.AmzSdkRequest.ToUpperInvariant()));
        }
    }
}
