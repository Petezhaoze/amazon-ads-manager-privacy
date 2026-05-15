using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

var connectionString = Environment.GetEnvironmentVariable("ANALYTICS_DB_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ANALYTICS_DB_CONNECTION_STRING is required.");
    return 2;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ApplySqlSchema <schema.sql>");
    return 2;
}

var schemaPath = Path.GetFullPath(args[0]);
var sql = await File.ReadAllTextAsync(schemaPath);
var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
    .Select(batch => batch.Trim())
    .Where(batch => batch.Length > 0)
    .ToList();

await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();

foreach (var batch in batches)
{
    await using var command = connection.CreateCommand();
    command.CommandText = batch;
    command.CommandTimeout = 120;
    await command.ExecuteNonQueryAsync();
}

Console.WriteLine($"Applied {batches.Count} schema batches to analytics database.");
return 0;
