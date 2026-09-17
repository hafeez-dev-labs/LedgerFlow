using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerFlow.Migrations;

public partial class AddReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE reconciliation_runs (
                id uuid NOT NULL,
                idempotency_key varchar(200) NOT NULL,
                started_at timestamp with time zone NOT NULL,
                completed_at timestamp with time zone NULL,
                CONSTRAINT pk_reconciliation_runs PRIMARY KEY (id)
            );

            CREATE UNIQUE INDEX ix_reconciliation_runs_idempotency_key
                ON reconciliation_runs (idempotency_key);

            CREATE TABLE reconciliation_results (
                id uuid NOT NULL,
                reconciliation_run_id uuid NOT NULL,
                status varchar(30) NOT NULL,
                external_transaction_id varchar(200) NULL,
                transaction_id uuid NULL,
                internal_amount numeric(19,4) NULL,
                external_amount numeric(19,4) NULL,
                internal_currency varchar(3) NULL,
                external_currency varchar(3) NULL,
                explanation varchar(1000) NOT NULL,
                CONSTRAINT pk_reconciliation_results PRIMARY KEY (id),
                CONSTRAINT fk_reconciliation_results_runs_reconciliation_run_id
                    FOREIGN KEY (reconciliation_run_id) REFERENCES reconciliation_runs (id) ON DELETE CASCADE
            );

            CREATE INDEX ix_reconciliation_results_run_status
                ON reconciliation_results (reconciliation_run_id, status);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS reconciliation_results;
            DROP TABLE IF EXISTS reconciliation_runs;
            """);
    }
}
