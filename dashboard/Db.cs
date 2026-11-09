using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaimCheck.Dashboard;

public sealed record LaneRow(string Name, int? Port, string? Upstream, string? AuditPath, string? Version,
    string? StartedUtc, string? PostureJson, string? LastSeen);

public sealed record RequestRow(long Id, string Lane, string Ts, string? Model, string? Tenant, int? Status,
    int? Ms, int TicketCount, int BodyLen, long SrcOffset, string SrcFile);

public sealed record HourRollup(string Hour, long Requests, long Errors, long TicketsTotal, long Ssn, long Fein,
    long Email, long Phone, long CreditCard, long CryptoWallet, long ApiKey, long Person, long Org, long Other);

public sealed record TicketSummary(string Type, long Count);
public sealed record NameCount(string Name, long Count);

/// <summary>Prepared audit line, parsed by IngestService from one JSONL record
/// (current wire format {"ts","body"}; tolerates future {lane,tenant,status,ms,model} extensions).</summary>
public sealed record LineRec(long Offset, string Ts, string Body, string? Model, string? Tenant,
    int? Status, int? Ms, Dictionary<string, int> Tickets);

/// <summary>SQLite sidecar (WAL). The JSONL audit files stay the archive — the DB holds metadata,
/// per-request ticket counts, and hourly rollups so pages never scan raw files for charts.
/// Writers: the IngestService loop only. Readers: page handlers (WAL → concurrent reads).</summary>
public sealed class DashboardDb
{
    private readonly string _cs;

    public DashboardDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Init();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS lanes(
          name TEXT PRIMARY KEY, port INTEGER, upstream TEXT, audit_path TEXT,
          version TEXT, started_utc TEXT, posture_json TEXT, last_seen TEXT);
        CREATE TABLE IF NOT EXISTS requests(
          id INTEGER PRIMARY KEY AUTOINCREMENT, lane TEXT NOT NULL, ts TEXT NOT NULL,
          model TEXT, tenant TEXT, status INTEGER, ms INTEGER,
          ticket_count INTEGER NOT NULL DEFAULT 0, body_len INTEGER NOT NULL DEFAULT 0,
          src_offset INTEGER NOT NULL, src_file TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_requests_lane_ts ON requests(lane, ts DESC, id DESC);
        CREATE TABLE IF NOT EXISTS tickets(
          request_id INTEGER NOT NULL, type TEXT NOT NULL, count INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_tickets_request ON tickets(request_id);
        CREATE INDEX IF NOT EXISTS ix_tickets_type ON tickets(type);
        CREATE TABLE IF NOT EXISTS rollups_hour(
          lane TEXT NOT NULL, hour TEXT NOT NULL,
          requests INTEGER NOT NULL DEFAULT 0, error_count INTEGER NOT NULL DEFAULT 0,
          tickets_total INTEGER NOT NULL DEFAULT 0,
          ssn INTEGER NOT NULL DEFAULT 0, fein INTEGER NOT NULL DEFAULT 0,
          email INTEGER NOT NULL DEFAULT 0, phone INTEGER NOT NULL DEFAULT 0,
          credit_card INTEGER NOT NULL DEFAULT 0, crypto_wallet INTEGER NOT NULL DEFAULT 0,
          api_key INTEGER NOT NULL DEFAULT 0, person INTEGER NOT NULL DEFAULT 0,
          org INTEGER NOT NULL DEFAULT 0, other INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(lane, hour));
        CREATE TABLE IF NOT EXISTS ingest_state(
          lane TEXT PRIMARY KEY, path TEXT NOT NULL, offset INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS vault_history(
          lane TEXT NOT NULL, ts TEXT NOT NULL, vault_entries INTEGER NOT NULL,
          PRIMARY KEY(lane, ts));
        """;

    private void Init()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    // ---- lane registry sync + status polls ----

    public void EnsureLaneRows(IEnumerable<LaneConfig> lanes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var l in lanes)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO lanes(name, port, audit_path) VALUES($n, $p, $a)
                ON CONFLICT(name) DO UPDATE SET port=excluded.port, audit_path=excluded.audit_path
                """;
            cmd.Parameters.AddWithValue("$n", l.Name);
            cmd.Parameters.AddWithValue("$p", (object?)l.Port ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$a", (object?)l.Audit ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpsertLaneStatus(string name, string version, string upstream, string startedUtc,
        string postureJson, string lastSeen, long? vaultEntries)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE lanes SET version=$v, upstream=$u, started_utc=$s, posture_json=$j, last_seen=$ls
                WHERE name=$n
                """;
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$u", upstream);
            cmd.Parameters.AddWithValue("$s", startedUtc);
            cmd.Parameters.AddWithValue("$j", postureJson);
            cmd.Parameters.AddWithValue("$ls", lastSeen);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.ExecuteNonQuery();
        }
        if (vaultEntries is { } ve)
        {
            // sample at most once per 5 minutes — enough for a trend, tiny table
            var cutoff = DateTime.UtcNow.AddMinutes(-5).ToString("O", CultureInfo.InvariantCulture);
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "SELECT MAX(ts) FROM vault_history WHERE lane=$n";
                q.Parameters.AddWithValue("$n", name);
                var last = q.ExecuteScalar() as string;
                if (last is null || string.CompareOrdinal(last, cutoff) < 0)
                {
                    using var ins = c.CreateCommand();
                    ins.Transaction = tx;
                    ins.CommandText = "INSERT OR REPLACE INTO vault_history(lane, ts, vault_entries) VALUES($n,$t,$e)";
                    ins.Parameters.AddWithValue("$n", name);
                    ins.Parameters.AddWithValue("$t", lastSeen);
                    ins.Parameters.AddWithValue("$e", ve);
                    ins.ExecuteNonQuery();
                }
            }
        }
        tx.Commit();
    }

    public List<LaneRow> GetLanes()
    {
        var rows = new List<LaneRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, port, upstream, audit_path, version, started_utc, posture_json, last_seen FROM lanes ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new LaneRow(r.GetString(0),
                r.IsDBNull(1) ? null : r.GetInt64(1) > int.MaxValue ? null : (int)r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7)));
        return rows;
    }

    // ---- ingest ----

    public (string Path, long Offset) GetIngestState(string lane)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path, offset FROM ingest_state WHERE lane=$n";
        cmd.Parameters.AddWithValue("$n", lane);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetInt64(1)) : ("", 0);
    }

    public void SetIngestState(string lane, string path, long offset)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ingest_state(lane, path, offset) VALUES($n,$p,$o) ON CONFLICT(lane) DO UPDATE SET path=excluded.path, offset=excluded.offset";
        cmd.Parameters.AddWithValue("$n", lane);
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$o", offset);
        cmd.ExecuteNonQuery();
    }

    private static readonly Regex TicketRx = new(@"^\[([A-Z][A-Z_]*?)_\d+\]$", RegexOptions.Compiled);

    private static (string col, long n) RollupCol(string type, long n) => type switch
    {
        "SSN" => ("ssn", n),
        "FEIN" or "EIN" or "TAX_ID" => ("fein", n),
        "EMAIL" or "EMAIL_ADDRESS" => ("email", n),
        "PHONE" or "PHONE_NUMBER" => ("phone", n),
        "CREDIT_CARD" => ("credit_card", n),
        "CRYPTO_WALLET" => ("crypto_wallet", n),
        "API_KEY" or "TOKEN" or "SECRET" or "PASSWORD" => ("api_key", n),
        "PERSON" => ("person", n),
        "ORG_ENTITY" or "ORG" or "ORGANIZATION" => ("org", n),
        _ => ("other", n),
    };

    /// <summary>Inserts a parsed batch (one transaction), updates hourly rollups, advances ingest offset.</summary>
    public int IngestBatch(string lane, string srcFile, long newOffset, List<LineRec> recs)
    {
        if (recs.Count == 0) { SetIngestState(lane, srcFile, newOffset); return 0; }
        using var c = Open();
        using var tx = c.BeginTransaction();
        int inserted = 0;
        foreach (var rec in recs)
        {
            long id;
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO requests(lane, ts, model, tenant, status, ms, ticket_count, body_len, src_offset, src_file)
                    VALUES($lane,$ts,$model,$tenant,$status,$ms,$tc,$bl,$off,$file) RETURNING id
                    """;
                cmd.Parameters.AddWithValue("$lane", lane);
                cmd.Parameters.AddWithValue("$ts", rec.Ts);
                cmd.Parameters.AddWithValue("$model", (object?)rec.Model ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tenant", (object?)rec.Tenant ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$status", (object?)rec.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ms", (object?)rec.Ms ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tc", rec.Tickets.Values.Sum());
                cmd.Parameters.AddWithValue("$bl", rec.Body.Length);
                cmd.Parameters.AddWithValue("$off", rec.Offset);
                cmd.Parameters.AddWithValue("$file", srcFile);
                id = (long)cmd.ExecuteScalar()!;
            }
            if (rec.Tickets.Count > 0)
            {
                using var tk = c.CreateCommand();
                tk.Transaction = tx;
                tk.CommandText = "INSERT INTO tickets(request_id, type, count) VALUES($r,$t,$c)";
                var pr = tk.Parameters.Add("$r", SqliteType.Integer);
                var pt = tk.Parameters.Add("$t", SqliteType.Text);
                var pc = tk.Parameters.Add("$c", SqliteType.Integer);
                foreach (var (type, n) in rec.Tickets)
                {
                    pr.Value = id; pt.Value = type; pc.Value = n;
                    tk.ExecuteNonQuery();
                }
            }
            // rollup
            var hour = TsToHour(rec.Ts);
            var cols = new Dictionary<string, long> { ["requests"] = 1, ["error_count"] = rec.Status is >= 400 ? 1 : 0, ["tickets_total"] = rec.Tickets.Values.Sum() };
            foreach (var (type, n) in rec.Tickets)
            {
                var (col, v) = RollupCol(type, n);
                cols[col] = cols.GetValueOrDefault(col) + v;
            }
            using (var ru = c.CreateCommand())
            {
                ru.Transaction = tx;
                var setCols = new[] { "requests", "error_count", "tickets_total", "ssn", "fein", "email", "phone", "credit_card", "crypto_wallet", "api_key", "person", "org", "other" };
                var insCols = string.Join(",", setCols);
                var insVals = string.Join(",", setCols.Select(col => "$" + col));
                var setUpd = string.Join(",", setCols.Select(col => $"{col}={col}+${col}"));
                ru.CommandText = $"INSERT INTO rollups_hour(lane, hour, {insCols}) VALUES($lane,$hour,{insVals}) ON CONFLICT(lane,hour) DO UPDATE SET {setUpd}";
                ru.Parameters.AddWithValue("$lane", lane);
                ru.Parameters.AddWithValue("$hour", hour);
                foreach (var col in setCols) ru.Parameters.AddWithValue("$" + col, cols.GetValueOrDefault(col));
                ru.ExecuteNonQuery();
            }
            inserted++;
        }
        using (var st = c.CreateCommand())
        {
            st.Transaction = tx;
            st.CommandText = "INSERT INTO ingest_state(lane, path, offset) VALUES($n,$p,$o) ON CONFLICT(lane) DO UPDATE SET path=excluded.path, offset=excluded.offset";
            st.Parameters.AddWithValue("$n", lane);
            st.Parameters.AddWithValue("$p", srcFile);
            st.Parameters.AddWithValue("$o", newOffset);
            st.ExecuteNonQuery();
        }
        tx.Commit();
        return inserted;
    }

    private static string TsToHour(string ts)
    {
        if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.UtcDateTime.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
    }

    // ---- queries ----

    public (long Requests, long Errors, long Tickets) Totals(string? lane, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(SUM(requests),0), COALESCE(SUM(error_count),0), COALESCE(SUM(tickets_total),0) FROM rollups_hour WHERE hour>=$cut {(lane != null ? "AND lane=$lane" : "")}";
        cmd.Parameters.AddWithValue("$cut", cutoff);
        if (lane != null) cmd.Parameters.AddWithValue("$lane", lane);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
    }

    public List<HourRollup> Hourly(string? lane, int hours)
    {
        var cutoff = DateTime.UtcNow.AddHours(-hours).ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
        var rows = new List<HourRollup>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT hour, SUM(requests), SUM(error_count), SUM(tickets_total), SUM(ssn), SUM(fein), SUM(email), SUM(phone), SUM(credit_card), SUM(crypto_wallet), SUM(api_key), SUM(person), SUM(org), SUM(other) FROM rollups_hour WHERE hour>=$cut {(lane != null ? "AND lane=$lane" : "")} GROUP BY hour ORDER BY hour";
        cmd.Parameters.AddWithValue("$cut", cutoff);
        if (lane != null) cmd.Parameters.AddWithValue("$lane", lane);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new HourRollup(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13)));
        return rows;
    }

    public List<(string Day, HourRollup R)> Daily(string? lane, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var rows = new List<(string, HourRollup)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT substr(hour,1,10) d, SUM(requests), SUM(error_count), SUM(tickets_total), SUM(ssn), SUM(fein), SUM(email), SUM(phone), SUM(credit_card), SUM(crypto_wallet), SUM(api_key), SUM(person), SUM(org), SUM(other) FROM rollups_hour WHERE substr(hour,1,10)>=$cut {(lane != null ? "AND lane=$lane" : "")} GROUP BY d ORDER BY d DESC";
        cmd.Parameters.AddWithValue("$cut", cutoff);
        if (lane != null) cmd.Parameters.AddWithValue("$lane", lane);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((r.GetString(0), new HourRollup(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13))));
        return rows;
    }

    public List<RequestRow> RecentRequests(string? lane, int limit, int skip = 0)
    {
        var rows = new List<RequestRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, lane, ts, model, tenant, status, ms, ticket_count, body_len, src_offset, src_file FROM requests {(lane != null ? "WHERE lane=$lane" : "")} ORDER BY ts DESC, id DESC LIMIT $lim OFFSET $skip";
        if (lane != null) cmd.Parameters.AddWithValue("$lane", lane);
        cmd.Parameters.AddWithValue("$lim", limit);
        cmd.Parameters.AddWithValue("$skip", skip);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(ReadRequest(r));
        return rows;
    }

    public RequestRow? GetRequest(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, lane, ts, model, tenant, status, ms, ticket_count, body_len, src_offset, src_file FROM requests WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRequest(r) : null;
    }

    private static RequestRow ReadRequest(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetInt64(5) > int.MaxValue ? null : (int)r.GetInt64(5),
        r.IsDBNull(6) ? null : r.GetInt64(6) > int.MaxValue ? null : (int)r.GetInt64(6),
        r.GetInt32(7), r.GetInt32(8), r.GetInt64(9), r.GetString(10));

    public List<TicketSummary> TicketTypes(string? lane)
    {
        var rows = new List<TicketSummary>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT t.type, SUM(t.count) FROM tickets t {(lane != null ? "JOIN requests rq ON rq.id=t.request_id AND rq.lane=$lane" : "")} GROUP BY t.type ORDER BY 2 DESC";
        if (lane != null) cmd.Parameters.AddWithValue("$lane", lane);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new TicketSummary(r.GetString(0), r.GetInt64(1)));
        return rows;
    }

    public List<TicketSummary> TicketsFor(long requestId)
    {
        var rows = new List<TicketSummary>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT type, count FROM tickets WHERE request_id=$id ORDER BY count DESC, type";
        cmd.Parameters.AddWithValue("$id", requestId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new TicketSummary(r.GetString(0), r.GetInt64(1)));
        return rows;
    }

    public List<NameCount> ModelMix(string? lane)
    {
        var rows = new List<NameCount>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(model,'(unknown)'), COUNT(*) FROM requests {(lane != null ? "WHERE lane=$lane" : "")} GROUP BY 1 ORDER BY 2 DESC LIMIT 12";
        if (lane != null) cmd.Parameters.AddWithValue("$lane", lane);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new NameCount(r.GetString(0), r.GetInt64(1)));
        return rows;
    }

    public List<NameCount> TopTenants()
    {
        var rows = new List<NameCount>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT tenant, COUNT(*) FROM requests WHERE tenant IS NOT NULL AND tenant<>'' GROUP BY tenant ORDER BY 2 DESC LIMIT 12";
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add(new NameCount(r.GetString(0), r.GetInt64(1)));
        return rows;
    }

    public long RequestCount(string? lane)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM requests {(lane != null ? "WHERE lane=$lane" : "")}";
        if (lane != null) cmd.Parameters.AddWithValue("$lane", lane);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<(string Ts, long Entries)> VaultHistory(string lane, int max = 48)
    {
        var rows = new List<(string, long)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ts, vault_entries FROM vault_history WHERE lane=$n ORDER BY ts DESC LIMIT $m";
        cmd.Parameters.AddWithValue("$n", lane);
        cmd.Parameters.AddWithValue("$m", max);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetString(0), r.GetInt64(1)));
        rows.Reverse();
        return rows;
    }
}
