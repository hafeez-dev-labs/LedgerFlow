using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerFlow.Migrations;

public partial class AddTransactionStateTransitions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE transaction_state_transitions (
                id uuid NOT NULL,
                transaction_id uuid NOT NULL,
                from_status varchar(20) NOT NULL,
                to_status varchar(20) NOT NULL,
                transitioned_at timestamp with time zone NOT NULL,
                failure_reason varchar(1000),
                CONSTRAINT pk_transaction_state_transitions PRIMARY KEY (id),
                CONSTRAINT fk_transaction_state_transitions_transaction FOREIGN KEY (transaction_id) REFERENCES transactions (id) ON DELETE RESTRICT,
                CONSTRAINT ck_transaction_state_transition_status CHECK (
                    (from_status = 'Pending' AND to_status = 'Processing') OR
                    (from_status = 'Processing' AND to_status IN ('Completed', 'Failed'))
                ),
                CONSTRAINT ck_transaction_state_transition_failure_reason CHECK (
                    (to_status = 'Failed' AND failure_reason IS NOT NULL AND char_length(trim(failure_reason)) > 0) OR
                    (to_status <> 'Failed' AND failure_reason IS NULL)
                )
            );

            CREATE INDEX ix_transaction_state_transitions_transaction_id_transitioned_at
            ON transaction_state_transitions (transaction_id, transitioned_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS transaction_state_transitions;
            """);
    }
}
