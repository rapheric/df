using System;
using System.Data;
using System.Text.Json;
using MySqlConnector;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: dotnet run -- <DEFERRAL_NUMBER>");
            return 2;
        }
        var defNumber = args[0];
        var mode = args.Length > 1 ? args[1] : "deferral"; // 'deferral' or 'approvers' or 'documents' or 'full'
        var connStr = "Server=localhost;Database=dcl_ncba;User=root;Password=password123;";

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

                // full
                var full = new System.Collections.Generic.Dictionary<string, object?>();
                full["deferral"] = defDict;
                full["approvers"] = approvers;
                full["extensions"] = extensions;
                full["documents"] = documents;
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
}
