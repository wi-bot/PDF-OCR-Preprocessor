using System.Text.Json;
using Microsoft.Data.Sqlite;
using PdfOcrPreprocessor.Core;

namespace PdfOcrPreprocessor.Desktop;

public static class ProofStore
{
    public static string RoundTrip(string databasePath, PageEvidence evidence)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS Evidence (Id TEXT PRIMARY KEY, PageNumber INTEGER NOT NULL, UserUnit REAL NOT NULL, Payload TEXT NOT NULL)";
        create.ExecuteNonQuery();
        var identifier = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(evidence);
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO Evidence(Id, PageNumber, UserUnit, Payload) VALUES ($id, $page, $unit, $payload)";
        insert.Parameters.AddWithValue("$id", identifier);
        insert.Parameters.AddWithValue("$page", evidence.PageNumber);
        insert.Parameters.AddWithValue("$unit", evidence.Geometry.UserUnit);
        insert.Parameters.AddWithValue("$payload", payload);
        if (insert.ExecuteNonQuery() != 1) throw new InvalidDataException("SQLite insert failed.");
        transaction.Commit();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id, PageNumber, UserUnit, Payload FROM Evidence WHERE Id = $id";
        read.Parameters.AddWithValue("$id", identifier);
        using var reader = read.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("SQLite payload missing.");
        if (reader.GetString(0) != identifier || reader.GetInt32(1) != evidence.PageNumber || reader.GetDouble(2) != evidence.Geometry.UserUnit)
            throw new InvalidDataException("SQLite identifier or numeric round trip failed.");
        var stored = reader.GetString(3);
        var decoded = JsonSerializer.Deserialize<PageEvidence>(stored) ?? throw new InvalidDataException("SQLite payload invalid.");
        if (stored != payload || JsonSerializer.Serialize(decoded) != payload) throw new InvalidDataException("SQLite evidence round trip changed content.");
        return connection.ServerVersion;
    }
}