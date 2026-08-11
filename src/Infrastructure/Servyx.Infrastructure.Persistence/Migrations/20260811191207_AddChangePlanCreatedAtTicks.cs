using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Servyx.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChangePlanCreatedAtTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The single-column index is REPLACED, not supplemented: IX_ChangePlans_ServerId_CreatedAtTicks
            // leads with ServerId, so it answers every lookup the old one did (including the foreign key's)
            // while also serving the ordering the history listing needs. Two indexes over overlapping columns
            // would be a second copy to maintain on every insert for no additional query.
            migrationBuilder.DropIndex(
                name: "IX_ChangePlans_ServerId",
                table: "ChangePlans");

            // CreatedAt as UTC ticks. The reason this column exists rather than an ORDER BY over CreatedAt is
            // on ChangePlanRecord.CreatedAtTicks: EF Core's SQLite provider refuses to translate a comparison
            // over a DateTimeOffset column, which had ListRecentAsync loading a server's entire plan history
            // into memory to return the newest page of it.
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtTicks",
                table: "ChangePlans",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // THE BACKFILL, and it is not optional. The scaffolder's `defaultValue: 0L` leaves every
            // pre-existing plan claiming it was created at 01-01-0001, which does not fail anything, throw
            // anything or look wrong anywhere — it just silently sorts the whole of an operator's existing
            // change history below every plan created after this migration, in an arbitrary order among
            // itself. A history view that quietly lies about order is worse than one that errors.
            //
            // Written against Microsoft.Data.Sqlite's storage format for DateTimeOffset, which is
            // "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz" — trailing zeros trimmed from the fraction, the '.' omitted
            // entirely when the fraction is zero, and always an offset suffix of the form +HH:MM / -HH:MM.
            // Hence the two halves below:
            //
            //   * whole seconds, via strftime('%s', ...), which parses the offset suffix and returns UTC
            //     Unix seconds. Shifted by 62135596800 (the seconds from 0001-01-01 to 1970-01-01) and scaled
            //     to ticks.
            //
            //   * the fractional part, extracted textually rather than through a date function, because
            //     strftime('%f') caps out at milliseconds and CreatedAt is written from a TimeProvider with
            //     100ns resolution. The substring after '.' is cut at the offset's sign, right-padded to
            //     seven digits, and read as an integer — so '.123' becomes 1230000 ticks rather than 123.
            //
            // A CreatedAt this cannot parse yields NULL and violates the NOT NULL column, failing the
            // migration loudly. That is deliberate: the alternative is COALESCE(..., 0), which is exactly the
            // silent mis-ordering this backfill exists to prevent.
            migrationBuilder.Sql(
                """
                UPDATE ChangePlans
                SET CreatedAtTicks =
                    (CAST(strftime('%s', CreatedAt) AS INTEGER) + 62135596800) * 10000000
                    + CASE
                        WHEN instr(CreatedAt, '.') = 0 THEN 0
                        ELSE CAST(
                            substr(
                                substr(
                                    substr(CreatedAt, instr(CreatedAt, '.') + 1),
                                    1,
                                    CASE
                                        WHEN instr(substr(CreatedAt, instr(CreatedAt, '.') + 1), '+') > 0
                                            THEN instr(substr(CreatedAt, instr(CreatedAt, '.') + 1), '+') - 1
                                        WHEN instr(substr(CreatedAt, instr(CreatedAt, '.') + 1), '-') > 0
                                            THEN instr(substr(CreatedAt, instr(CreatedAt, '.') + 1), '-') - 1
                                        ELSE length(substr(CreatedAt, instr(CreatedAt, '.') + 1))
                                    END)
                                || '0000000',
                                1,
                                7) AS INTEGER)
                      END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ChangePlans_ServerId_CreatedAtTicks",
                table: "ChangePlans",
                columns: new[] { "ServerId", "CreatedAtTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChangePlans_ServerId_CreatedAtTicks",
                table: "ChangePlans");

            migrationBuilder.DropColumn(
                name: "CreatedAtTicks",
                table: "ChangePlans");

            migrationBuilder.CreateIndex(
                name: "IX_ChangePlans_ServerId",
                table: "ChangePlans",
                column: "ServerId");
        }
    }
}
