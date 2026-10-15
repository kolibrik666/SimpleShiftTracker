using System.IO;
using Tracker.Core;

namespace Tracker.App;

internal static class UiErrors
{
    public static string Describe(Exception exception) => exception switch
    {
        TrackerValidationException => exception.Message,
        UnauthorizedAccessException => "Access was denied. Choose a writable folder or check access to your data folder, then try again.",
        IOException => "The file could not be saved. Check free disk space and whether the file is open in another app, then try again.",
        Microsoft.Data.Sqlite.SqliteException or Microsoft.EntityFrameworkCore.DbUpdateException =>
            "Your changes could not be saved. Check free disk space and access to the data folder, then try again. Details are in tracker.log.",
        _ => "The action could not be completed. Please try again. Details are in tracker.log."
    };
}
