using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// A run's hold on a submission it works on (osdu/docs/reference/concepts/submissions.md, Recovering a stopped submission):
/// a shared, session-owned application lock on the module's database (<c>sp_getapplock</c>), kept on a connection of its
/// own for as long as the run works on the submission, and released when the hold is disposed. Any number of runs hold one
/// submission at once, as a fan-out's coordinator and its members do. A process that stops frees its holds with its
/// connections, whatever stopped it, which is how a later run of the flow tells a submission somebody still works on from
/// one whose run has ended (<see cref="ILedgerSubmissions.TakeOverSubmissionAsync"/>). A hold reads and writes nothing
/// of the ledger.
/// </summary>
public sealed class SubmissionHold : IAsyncDisposable, IDisposable
{
    private readonly OsduDbContext _db;
    private int _released;

    internal SubmissionHold(OsduDbContext db, Guid submissionId)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        SubmissionId = submissionId;
    }

    /// <summary>The submission held.</summary>
    public Guid SubmissionId { get; }

    /// <summary>
    /// The application lock a submission is held under: one per submission, in the module's database. Application locks are
    /// scoped to the database they are taken in, which is the one the ledger keeps the submission in.
    /// </summary>
    internal static string Resource(Guid submissionId) => "SqlFlow.Delivery.Submission:" + submissionId.ToString("D");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return;
        }

        try
        {
            var connection = (SqlConnection)_db.Database.GetDbConnection();
            if (connection.State == ConnectionState.Open)
            {
                try
                {
                    await using var command = Release(connection);
                    await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    Discard(connection);
                }
            }
        }
        finally
        {
            await _db.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return;
        }

        try
        {
            var connection = (SqlConnection)_db.Database.GetDbConnection();
            if (connection.State == ConnectionState.Open)
            {
                try
                {
                    using var command = Release(connection);
                    command.ExecuteNonQuery();
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    Discard(connection);
                }
            }
        }
        finally
        {
            _db.Dispose();
        }
    }

    private SqlCommand Release(SqlConnection connection) => SqlServerLedgerBulk.ReleaseSubmission(connection, Resource(SubmissionId));

    /// <summary>
    /// A release that did not reach the server leaves the lock with the session. The connection must then not go back to the
    /// pool still holding it, where the submission would read as worked on until the pooled connection happened to be reset:
    /// clearing its pool closes the connection when the context lets it go, and the session's locks end with it.
    /// </summary>
    private static void Discard(SqlConnection connection) => SqlConnection.ClearPool(connection);
}
