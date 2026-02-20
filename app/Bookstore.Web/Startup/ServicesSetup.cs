using Amazon.Rekognition;
using Amazon.S3;
using Amazon.SecretsManager.Model;
using Amazon.SecretsManager;
using Bookstore.Data;
using Bookstore.Domain.AdminUser;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Npgsql;


namespace Bookstore.Web.Startup
{
    public static class ServicesSetup
    {
        public static WebApplicationBuilder ConfigureServices(this WebApplicationBuilder builder)
        {
            builder.Services.AddControllersWithViews(x =>
            {
                x.Filters.Add(new AuthorizeFilter());
                x.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            });

            builder.Services.AddDefaultAWSOptions(builder.Configuration.GetAWSOptions());
            builder.Services.AddAWSService<IAmazonS3>();
            builder.Services.AddAWSService<IAmazonRekognition>();

            var connString = GetDatabaseConnectionString(builder.Configuration);
            builder.Services.AddDbContext<ApplicationDbContext>(option => option.UseNpgsql(connString));
            builder.Services.AddSession();

            return builder;
        }

        // If we find a non-empty connection string in appsettings, use it, otherwise
        // attempt to build it from data in Secrets Manager
        private static string GetDatabaseConnectionString(ConfigurationManager configuration)
        {
            // PostgreSQL database secret in AWS Secrets Manager for the target Aurora PostgreSQL database.
            // This ARN points directly to the secret containing the credentials for the migrated database.
            const string DbSecretsParameterName = "arn:aws:secretsmanager:us-east-1:963887584822:secret:atx-db-modernization-secret-aurora-admin-ic0Tr5";

            var connString = configuration.GetConnectionString("BookstoreDbDefaultConnection");
            if (!string.IsNullOrEmpty(connString))
            {
                Console.WriteLine("Using localdb connection string");
                return connString;
            }

            try
            {
                var dbSecretId = DbSecretsParameterName;
                Console.WriteLine($"Reading db credentials from secret {dbSecretId}");

                // Read the db secrets from Secrets Manager. The secret provides the host,
                // port, username, and password, which we use to build the connection string for PostgreSQL.
                // For this code to work locally, appsettings.json must contain an AWS object with profile and
                // region info. When deployed to an EC2 instance, credentials and region will be inferred from
                // the instance profile applied to the instance.
                IAmazonSecretsManager secretsManagerClient;
                var options = configuration.GetAWSOptions();
                if (options != null)
                {
                    // local "integrated" debug mode using credentials/region in appsettings
                    secretsManagerClient = options.CreateServiceClient<IAmazonSecretsManager>();
                }
                else
                {
                    // deployed mode using credentials/region inferred on host
                    secretsManagerClient = new AmazonSecretsManagerClient();
                }
                var response = secretsManagerClient.GetSecretValueAsync(new GetSecretValueRequest
                {
                    SecretId = dbSecretId
                }).Result;

                var dbSecrets = JsonSerializer.Deserialize<DbSecrets>(response.SecretString, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var builder = new NpgsqlConnectionStringBuilder
                {
                    Host = dbSecrets.Host,
                    Port = dbSecrets.Port,
                    Database = "postgres",
                    Username = dbSecrets.Username,
                    Password = dbSecrets.Password
                };

                connString = builder.ConnectionString;
            }
            catch (AmazonSecretsManagerException e)
            {
                Console.WriteLine($"Failed to read secret {DbSecretsParameterName}, error {e.Message}, inner {e.InnerException.Message}");
            }
            catch (JsonException e)
            {
                Console.WriteLine($"Failed to parse content for secret {DbSecretsParameterName}, error {e.Message}");
            }

            return connString;
        }
    }
}