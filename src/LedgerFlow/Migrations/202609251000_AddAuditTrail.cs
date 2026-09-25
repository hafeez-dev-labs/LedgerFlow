using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerFlow.Migrations;

public partial class AddAuditTrail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE audit_records (
                id uuid NOT NULL,
                event_type varchar(100) NOT NULL,
                aggregate_type varchar(100) NOT NULL,
                aggregate_id uuid NOT NULL,
                transaction_id uuid NULL,
                correlation_id varchar(200) NULL,
                occurred_at timestamp with time zone NOT NULL,
                payload jsonb NOT NULL,
                CONSTRAINT pk_audit_records PRIMARY KEY (id)
            );

            CREATE INDEX ix_audit_records_transaction_occurred
                ON audit_records (transaction_id, occurred_at);

            CREATE INDEX ix_audit_records_aggregate_occurred
                ON audit_records (aggregate_type, aggregate_id, occurred_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE IF EXISTS audit_records;");
    }
}
