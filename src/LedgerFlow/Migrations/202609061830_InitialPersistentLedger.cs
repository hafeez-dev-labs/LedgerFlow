using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerFlow.Migrations;

public partial class InitialPersistentLedger : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE accounts (
                id varchar(100) NOT NULL,
                currency varchar(3) NOT NULL,
                is_active boolean NOT NULL,
                CONSTRAINT pk_accounts PRIMARY KEY (id),
                CONSTRAINT ck_accounts_currency_length CHECK (char_length(currency) = 3)
            );

            CREATE TABLE transactions (
                id uuid NOT NULL,
                from_account_id varchar(100) NOT NULL,
                to_account_id varchar(100) NOT NULL,
                amount numeric(19,4) NOT NULL,
                currency varchar(3) NOT NULL,
                status varchar(20) NOT NULL,
                created_at timestamp with time zone NOT NULL,
                idempotency_key varchar(200) NOT NULL,
                CONSTRAINT pk_transactions PRIMARY KEY (id),
                CONSTRAINT ux_transactions_idempotency_key UNIQUE (idempotency_key),
                CONSTRAINT ck_transactions_amount_positive CHECK (amount > 0),
                CONSTRAINT ck_transactions_distinct_accounts CHECK (from_account_id <> to_account_id),
                CONSTRAINT ck_transactions_currency_length CHECK (char_length(currency) = 3),
                CONSTRAINT fk_transactions_from_account FOREIGN KEY (from_account_id) REFERENCES accounts (id),
                CONSTRAINT fk_transactions_to_account FOREIGN KEY (to_account_id) REFERENCES accounts (id)
            );

            CREATE TABLE journal_entries (
                id uuid NOT NULL,
                transaction_id uuid NOT NULL,
                account_id varchar(100) NOT NULL,
                type varchar(10) NOT NULL,
                amount numeric(19,4) NOT NULL,
                currency varchar(3) NOT NULL,
                posted_at timestamp with time zone NOT NULL,
                CONSTRAINT pk_journal_entries PRIMARY KEY (id),
                CONSTRAINT ck_journal_entries_amount_positive CHECK (amount > 0),
                CONSTRAINT ck_journal_entries_currency_length CHECK (char_length(currency) = 3),
                CONSTRAINT ck_journal_entries_type CHECK (type IN ('Debit', 'Credit')),
                CONSTRAINT fk_journal_entries_transaction FOREIGN KEY (transaction_id) REFERENCES transactions (id),
                CONSTRAINT fk_journal_entries_account FOREIGN KEY (account_id) REFERENCES accounts (id)
            );

            CREATE INDEX ix_transactions_from_account_id ON transactions (from_account_id);
            CREATE INDEX ix_transactions_to_account_id ON transactions (to_account_id);
            CREATE INDEX ix_journal_entries_transaction_id ON journal_entries (transaction_id);
            CREATE INDEX ix_journal_entries_account_id ON journal_entries (account_id);

            CREATE OR REPLACE FUNCTION prevent_journal_entry_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'Posted journal entries are immutable';
            END;
            $$;

            CREATE TRIGGER journal_entries_immutable
            BEFORE UPDATE OR DELETE ON journal_entries
            FOR EACH ROW
            EXECUTE FUNCTION prevent_journal_entry_mutation();

            CREATE OR REPLACE FUNCTION validate_journal_balance()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                transaction_id_value uuid;
                debit_total numeric(19,4);
                credit_total numeric(19,4);
            BEGIN
                transaction_id_value := COALESCE(NEW.transaction_id, OLD.transaction_id);

                SELECT
                    COALESCE(SUM(CASE WHEN type = 'Debit' THEN amount ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN type = 'Credit' THEN amount ELSE 0 END), 0)
                INTO debit_total, credit_total
                FROM journal_entries
                WHERE transaction_id = transaction_id_value;

                IF debit_total <> credit_total OR debit_total <= 0 THEN
                    RAISE EXCEPTION 'Journal entries for transaction % must balance', transaction_id_value;
                END IF;

                RETURN NULL;
            END;
            $$;

            CREATE CONSTRAINT TRIGGER journal_entries_must_balance
            AFTER INSERT OR UPDATE OR DELETE ON journal_entries
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION validate_journal_balance();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS journal_entries_must_balance ON journal_entries;
            DROP TRIGGER IF EXISTS journal_entries_immutable ON journal_entries;
            DROP FUNCTION IF EXISTS validate_journal_balance();
            DROP FUNCTION IF EXISTS prevent_journal_entry_mutation();
            DROP TABLE IF EXISTS journal_entries;
            DROP TABLE IF EXISTS transactions;
            DROP TABLE IF EXISTS accounts;
            """);
    }
}
