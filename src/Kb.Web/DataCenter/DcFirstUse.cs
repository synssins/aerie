using Kb.Data;
using Microsoft.EntityFrameworkCore;

namespace Kb.Web.DataCenter;

/// <summary>
/// The surface gives an object its row the first time it meets it: a compatible id for a page, a file or a space, a number for a
/// version, an id for a label. Two requests that meet the same object at the same moment would both add the row, and the second
/// insert would fail on the unique index: the answer was still right (the loser read the winner's row), but the database error was
/// logged each time, and whatever else the loser was adding in the same step was lost with it. So within one server these additions
/// take turns: each looks for what is missing only once it is its turn. Another server can still be first; then the addition is
/// let go and looked for again.
/// </summary>
internal static class DcFirstUse
{
    private static readonly SemaphoreSlim Turn = new(1, 1);

    /// <param name="add">Finds what is still missing, adds the rows to the context and returns them; empty when nothing is missing.</param>
    public static async Task AddAsync(KbDbContext db, Func<Task<IReadOnlyList<object>>> add, CancellationToken ct)
    {
        await Turn.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var added = await add();
                if (added.Count == 0) return;
                try { await db.SaveChangesAsync(ct); return; }
                catch (DbUpdateException) { foreach (var row in added) db.Entry(row).State = EntityState.Detached; } // another server was first
            }
        }
        finally { Turn.Release(); }
    }
}
