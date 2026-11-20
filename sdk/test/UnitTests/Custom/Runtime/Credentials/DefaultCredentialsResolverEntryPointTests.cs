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

using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Credentials;
using Amazon.Runtime.Identity;
using Amazon.S3;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the behavior of <see cref="DefaultAWSCredentialsIdentityResolver"/>'s entry points when the
    /// credentials are already cached and when they are not: every entry point (sync, async, and the async
    /// <see cref="IIdentityResolver"/> implementation) returns the cached instance, notices environment
    /// changes, and reports cancellation and resolution failures through the returned task.
    /// </summary>
    [TestClass]
    public class DefaultCredentialsResolverEntryPointTests
    {
        private const string AwsProfileVariable = "AWS_PROFILE";

        private static readonly string[] ChainVariables =
        {
            AwsProfileVariable,
            EnvironmentVariablesAWSCredentials.ENVIRONMENT_VARIABLE_ACCESSKEY,
            EnvironmentVariablesAWSCredentials.ENVIRONMENT_VARIABLE_SECRETKEY,
            EnvironmentVariablesAWSCredentials.LEGACY_ENVIRONMENT_VARIABLE_SECRETKEY,
            EnvironmentVariablesAWSCredentials.ENVIRONMENT_VARIABLE_SESSION_TOKEN,
            GenericContainerCredentials.RelativeURIEnvVariable,
        };

        private string[] _savedVariables;

        [TestInitialize]
        public void TestInitialize()
        {
            _savedVariables = new string[ChainVariables.Length];
            for (int i = 0; i < ChainVariables.Length; i++)
            {
                _savedVariables[i] = Environment.GetEnvironmentVariable(ChainVariables[i]);
                Environment.SetEnvironmentVariable(ChainVariables[i], null);
            }
        }

        [TestCleanup]
        public void TestCleanup()
        {
            for (int i = 0; i < ChainVariables.Length; i++)
            {
                Environment.SetEnvironmentVariable(ChainVariables[i], _savedVariables[i]);
            }
        }

        public enum EntryPoint { Sync, Async, IdentityResolverAsync }

        [TestMethod]
        [DataRow(EntryPoint.Sync)]
        [DataRow(EntryPoint.Async)]
        [DataRow(EntryPoint.IdentityResolverAsync)]
        public async Task CachedEnvironmentCredentialsAreReturnedUntilTheEnvironmentChanges(EntryPoint entryPoint)
        {
            SetEnvironmentCredentials("AKID_FIRST", "SECRET_FIRST");
            using (var resolver = new DefaultAWSCredentialsIdentityResolver())
            {
                var first = await Resolve(resolver, entryPoint, clientConfig: null);
                var second = await Resolve(resolver, entryPoint, clientConfig: null);

                Assert.IsInstanceOfType(first, typeof(EnvironmentVariablesAWSCredentials));
                Assert.AreSame(first, second);
                Assert.AreEqual("AKID_FIRST", first.GetCredentials().AccessKey);

                SetEnvironmentCredentials("AKID_SECOND", "SECRET_SECOND");
                var third = await Resolve(resolver, entryPoint, clientConfig: null);

                Assert.AreNotSame(first, third);
                Assert.AreEqual("AKID_SECOND", third.GetCredentials().AccessKey);
                Assert.AreSame(third, await Resolve(resolver, entryPoint, clientConfig: null));

                // A session token appearing is an environment change too.
                Environment.SetEnvironmentVariable(EnvironmentVariablesAWSCredentials.ENVIRONMENT_VARIABLE_SESSION_TOKEN, "TOKEN");
                var fourth = await Resolve(resolver, entryPoint, clientConfig: null);
                Assert.AreNotSame(third, fourth);
                Assert.AreEqual("TOKEN", fourth.GetCredentials().Token);
            }
        }

        [TestMethod]
        public async Task AllEntryPointsShareTheCache()
        {
            SetEnvironmentCredentials("AKID_SHARED", "SECRET_SHARED");
            using (var resolver = new DefaultAWSCredentialsIdentityResolver())
            {
                var viaInterface = await ((IIdentityResolver)resolver).ResolveIdentityAsync(null);
                Assert.AreSame(viaInterface, resolver.ResolveIdentity(null));
                Assert.AreSame(viaInterface, await resolver.ResolveIdentityAsync(null));
                Assert.AreSame(viaInterface, ((IIdentityResolver)resolver).ResolveIdentity(null));
            }
        }

        [TestMethod]
        [DataRow(EntryPoint.Async)]
        [DataRow(EntryPoint.IdentityResolverAsync)]
        public async Task CachedCredentialsAreReturnedEvenWhenTheTokenIsCanceled(EntryPoint entryPoint)
        {
            SetEnvironmentCredentials("AKID", "SECRET");
            using (var resolver = new DefaultAWSCredentialsIdentityResolver())
            {
                var cached = resolver.ResolveIdentity(null);

                var task = StartAsync(resolver, entryPoint, null, new CancellationToken(canceled: true));
                await task;
                Assert.AreSame(cached, ResultOf(task));
            }
        }

        [TestMethod]
        [DataRow(EntryPoint.Async)]
        [DataRow(EntryPoint.IdentityResolverAsync)]
        public async Task UncachedResolutionWithACanceledTokenReturnsACanceledTask(EntryPoint entryPoint)
        {
            SetEnvironmentCredentials("AKID", "SECRET");
            using (var resolver = new DefaultAWSCredentialsIdentityResolver())
            {
                // Nothing is cached yet, so the resolver has to wait on its lock, which observes the token.
                // The call itself must not throw.
                var task = StartAsync(resolver, entryPoint, null, new CancellationToken(canceled: true));

                await Assert.ThrowsAsync<OperationCanceledException>(() => task);
                Assert.IsTrue(task.IsCanceled);
            }
        }

        [TestMethod]
        [DataRow(EntryPoint.Async)]
        [DataRow(EntryPoint.IdentityResolverAsync)]
        public async Task MissingProfileFaultsTheReturnedTask(EntryPoint entryPoint)
        {
            using (var fixture = new SharedCredentialsFileTestFixture("[real-profile]\naws_access_key_id = AKID\naws_secret_access_key = SECRET\n"))
            using (var resolver = new DefaultAWSCredentialsIdentityResolver())
            {
                var config = new AmazonS3Config { Profile = new Profile("missing-profile", fixture.CredentialsFilePath) };

                for (int i = 0; i < 2; i++)
                {
                    var task = StartAsync(resolver, entryPoint, config, CancellationToken.None);
                    await Assert.ThrowsExactlyAsync<AmazonClientException>(() => task);
                    Assert.IsTrue(task.IsFaulted);
                }
            }
        }

        [TestMethod]
        [DataRow(EntryPoint.Sync)]
        [DataRow(EntryPoint.Async)]
        [DataRow(EntryPoint.IdentityResolverAsync)]
        public async Task ProfileCredentialsAreCachedAndPreferredOverTheEnvironment(EntryPoint entryPoint)
        {
            SetEnvironmentCredentials("AKID_ENV", "SECRET_ENV");
            using (var fixture = new SharedCredentialsFileTestFixture("[test-profile]\naws_access_key_id = AKID_PROFILE\naws_secret_access_key = SECRET_PROFILE\n"))
            using (var resolver = new DefaultAWSCredentialsIdentityResolver())
            {
                // Cache the default chain's environment credentials first, so a profile lookup that wrongly
                // fell back to the default chain's cache would be caught.
                var fromEnvironment = resolver.ResolveIdentity(null);
                Assert.AreEqual("AKID_ENV", fromEnvironment.GetCredentials().AccessKey);

                var config = new AmazonS3Config { Profile = new Profile("test-profile", fixture.CredentialsFilePath) };
                var first = await Resolve(resolver, entryPoint, config);
                var second = await Resolve(resolver, entryPoint, config);

                Assert.AreEqual("AKID_PROFILE", first.GetCredentials().AccessKey);
                Assert.AreSame(first, second);
                Assert.AreSame(fromEnvironment, await Resolve(resolver, entryPoint, clientConfig: null));
            }
        }

        private static void SetEnvironmentCredentials(string accessKey, string secretKey)
        {
            Environment.SetEnvironmentVariable(EnvironmentVariablesAWSCredentials.ENVIRONMENT_VARIABLE_ACCESSKEY, accessKey);
            Environment.SetEnvironmentVariable(EnvironmentVariablesAWSCredentials.ENVIRONMENT_VARIABLE_SECRETKEY, secretKey);
        }

        private static async Task<AWSCredentials> Resolve(DefaultAWSCredentialsIdentityResolver resolver, EntryPoint entryPoint, IClientConfig clientConfig)
        {
            if (entryPoint == EntryPoint.Sync)
                return resolver.ResolveIdentity(clientConfig);

            var task = StartAsync(resolver, entryPoint, clientConfig, CancellationToken.None);
            await task;
            return ResultOf(task);
        }

        /// <summary>
        /// Starts the given async entry point and returns its task unchanged, so tests can inspect its state.
        /// </summary>
        private static Task StartAsync(DefaultAWSCredentialsIdentityResolver resolver, EntryPoint entryPoint, IClientConfig clientConfig, CancellationToken cancellationToken)
        {
            if (entryPoint == EntryPoint.Async)
                return resolver.ResolveIdentityAsync(clientConfig, cancellationToken);

            return ((IIdentityResolver)resolver).ResolveIdentityAsync(clientConfig, cancellationToken);
        }

        private static AWSCredentials ResultOf(Task task) =>
            task is Task<AWSCredentials> credentialsTask
                ? credentialsTask.Result
                : (AWSCredentials)((Task<BaseIdentity>)task).Result;
    }
}
