using System;
using System.Data;
using System.IO;
using System.Text.Json;
using MySqlConnector;

class Program
{
    static int Main(string[] args)
    {
        var connStr = ResolveConnectionString();

        if (args.Length > 0 && string.Equals(args[0], "--list-actioned", StringComparison.OrdinalIgnoreCase))
        {
            return ListActioned(connStr);
        }

        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet run -- <DEFERRAL_NUMBER> [deferral|approvers|documents|extensions|facilities|full]");
            Console.Error.WriteLine("   or: dotnet run -- --list-actioned");
            return 2;
        }

        var defNumber = args[0];
        var mode = args.Length > 1 ? args[1] : "deferral"; // 'deferral' or 'approvers' or 'documents' or 'extensions' or 'facilities' or 'full'

        try
        {
            using var conn = new MySqlConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM Deferrals WHERE DeferralNumber = @num LIMIT 1;";
            cmd.Parameters.AddWithValue("@num", defNumber);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                Console.WriteLine("null");
                return 0;
            }

            var defDict = new System.Collections.Generic.Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                defDict[name] = val;
            }
            // close reader before issuing a new command on the same connection
            reader.Close();

            var options = new JsonSerializerOptions { WriteIndented = true };

            if (mode == "deferral")
            {
                Console.WriteLine(JsonSerializer.Serialize(defDict, options));
                return 0;
            }

            // If asking for approvers/documents/full, fetch related data for the deferral id
            var defId = defDict.ContainsKey("Id") && defDict["Id"] != null ? defDict["Id"].ToString() : null;
            if (defId == null)
            {
                Console.WriteLine(JsonSerializer.Serialize(defDict, options));
                return 0;
            }

            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = "SELECT * FROM Approvers WHERE DeferralId = @id ORDER BY Name;";
                cmd2.Parameters.AddWithValue("@id", defId);
                using var r2 = cmd2.ExecuteReader();
                var approvers = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
                while (r2.Read())
                {
                    var ad = new System.Collections.Generic.Dictionary<string, object?>();
                    for (int j = 0; j < r2.FieldCount; j++)
                    {
                        var n = r2.GetName(j);
                        var v = r2.IsDBNull(j) ? null : r2.GetValue(j);
                        ad[n] = v;
                    }
                    approvers.Add(ad);
                }
                r2.Close();

                if (mode == "approvers")
                {
                    Console.WriteLine(JsonSerializer.Serialize(approvers, options));
                    return 0;
                }

                    // Optionally fetch extensions
                    using var cmd3 = conn.CreateCommand();
                    cmd3.CommandText = "SELECT * FROM Extensions WHERE DeferralId = @id ORDER BY CreatedAt DESC;";
                    cmd3.Parameters.AddWithValue("@id", defId);
                    using var r3 = cmd3.ExecuteReader();
                    var extensions = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
                    while (r3.Read())
                    {
                        var ed = new System.Collections.Generic.Dictionary<string, object?>();
                        for (int k = 0; k < r3.FieldCount; k++)
                        {
                            var n = r3.GetName(k);
                            var v = r3.IsDBNull(k) ? null : r3.GetValue(k);
                            ed[n] = v;
                        }
                        extensions.Add(ed);
                    }

                    r3.Close();

                    if (mode == "extensions")
                    {
                        Console.WriteLine(JsonSerializer.Serialize(extensions, options));
                        return 0;
                    }

                using var cmdDocs = conn.CreateCommand();
                cmdDocs.CommandText = "SELECT * FROM DeferralDocuments WHERE DeferralId = @id ORDER BY Name;";
                cmdDocs.Parameters.AddWithValue("@id", defId);
                using var rDocs = cmdDocs.ExecuteReader();
                var documents = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
                while (rDocs.Read())
                {
                    var dd = new System.Collections.Generic.Dictionary<string, object?>();
                    for (int k = 0; k < rDocs.FieldCount; k++)
                    {
                        var n = rDocs.GetName(k);
                        var v = rDocs.IsDBNull(k) ? null : rDocs.GetValue(k);
                        dd[n] = v;
                    }
                    documents.Add(dd);
                }

                if (mode == "documents")
                {
                    Console.WriteLine(JsonSerializer.Serialize(documents, options));
                    return 0;
                }

                rDocs.Close();

                using var cmdFacilities = conn.CreateCommand();
                cmdFacilities.CommandText = "SELECT * FROM Facilities WHERE DeferralId = @id ORDER BY Id;";
                cmdFacilities.Parameters.AddWithValue("@id", defId);
                using var rFacilities = cmdFacilities.ExecuteReader();
                var facilities = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
                while (rFacilities.Read())
                {
                    var fd = new System.Collections.Generic.Dictionary<string, object?>();
                    for (int k = 0; k < rFacilities.FieldCount; k++)
                    {
                        var n = rFacilities.GetName(k);
                        var v = rFacilities.IsDBNull(k) ? null : rFacilities.GetValue(k);
                        fd[n] = v;
                    }
                    facilities.Add(fd);
                }

                if (mode == "facilities")
                {
                    Console.WriteLine(JsonSerializer.Serialize(facilities, options));
                    return 0;
                }

                // full
                var full = new System.Collections.Generic.Dictionary<string, object?>();
                full["deferral"] = defDict;
                full["approvers"] = approvers;
                full["extensions"] = extensions;
                full["documents"] = documents;
                full["facilities"] = facilities;
                Console.WriteLine(JsonSerializer.Serialize(full, options));
                return 0;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.ToString());
            return 1;
        }
    }

    static string ResolveConnectionString()
    {
        var envConnectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? Environment.GetEnvironmentVariable("DEFAULT_CONNECTION");

        if (!string.IsNullOrWhiteSpace(envConnectionString))
        {
            return envConnectionString;
        }

        var configPaths = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "appsettings.Development.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "appsettings.json"))
        };

        foreach (var configPath in configPaths)
        {
            if (!File.Exists(configPath))
            {
                continue;
            }

            using var stream = File.OpenRead(configPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings) &&
                connectionStrings.TryGetProperty("DefaultConnection", out var defaultConnection))
            {
                var value = defaultConnection.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        throw new InvalidOperationException(
            "Default database connection is not configured. Set ConnectionStrings__DefaultConnection or DEFAULT_CONNECTION."
        );
    }

    static int ListActioned(string connStr)
    {
        try
        {
            using var conn = new MySqlConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT 
  d.DeferralNumber,
  d.Status,
  d.CustomerName,
  d.DclNumber,
  d.CreatedAt,
  d.UpdatedAt,
  COUNT(DISTINCT doc.Id) AS DocumentCount,
  COUNT(DISTINCT fac.Id) AS FacilityCount
FROM Deferrals d
LEFT JOIN DeferralDocuments doc ON doc.DeferralId = d.Id
LEFT JOIN Facilities fac ON fac.DeferralId = d.Id
WHERE d.Status IN ('Approved', 'Rejected', 'Closed', 'CloseRequested', 'CloseRequestedCreatorApproved', 'PartiallyApproved')
GROUP BY d.Id, d.DeferralNumber, d.Status, d.CustomerName, d.DclNumber, d.CreatedAt, d.UpdatedAt
ORDER BY d.UpdatedAt DESC
LIMIT 20;";

            using var reader = cmd.ExecuteReader();
            var rows = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new System.Collections.Generic.Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(row);
            }

            Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex);
            return 1;
        }
    }
}
