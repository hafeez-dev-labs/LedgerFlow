using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerFlow.Migrations;

public partial class AddRetryRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE retry_schedules (
                id uuid NOT NULL,
                transaction_id uuid NOT NULL,
                attempt_count integer NOT NULL,
                max_attempts integer NOT NULL,
                next_attempt_at timestamp with time zone NULL,
                last_failure_reason varchar(1000) NULL,
                status varchar(20) NOT NULL,
                resolved_at timestamp with time zone NULL,
                CONSTRAINT pk_retry_schedules PRIMARY KEY (id),
                CONSTRAINT fk_retry_schedules_transactions_transaction_id
                    FOREIGN KEY (transaction_id) REFERENCES transactions (id) ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX ix_retry_schedules_transaction_id
                ON retry_schedules (transaction_id);

            CREATE INDEX ix_retry_schedules_status_next_attempt_at
                ON retry_schedules (status, next_attempt_at);

            CREATE TABLE dead_letter_records (
                id uuid NOT NULL,
                transaction_id uuid NOT NULL,
                retry_attempts integer NOT NULL,
                failure_reason varchar(1000) NOT NULL,
                failed_at timestamp with time zone NOT NULL,
                resolved_at timestamp with time zone NULL,
                CONSTRAINT pk_dead_letter_records PRIMARY KEY (id),
                CONSTRAINT fk_dead_letter_records_transactions_transaction_id
                    FOREIGN KEY (transaction_id) REFERENCES transactions (id) ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX ix_dead_letter_records_transaction_id
                ON dead_letter_records (transaction_id);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS dead_letter_records;
            DROP TABLE IF EXISTS retry_schedules;
            """);
    }
}
