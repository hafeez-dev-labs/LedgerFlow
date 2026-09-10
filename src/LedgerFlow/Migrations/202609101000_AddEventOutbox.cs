using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LedgerFlow.Migrations;

public partial class AddEventOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE outbox_messages (
                id uuid NOT NULL,
                event_type varchar(200) NOT NULL,
                aggregate_id uuid NOT NULL,
                payload text NOT NULL,
                occurred_at timestamp with time zone NOT NULL,
                published_at timestamp with time zone NULL,
                CONSTRAINT pk_outbox_messages PRIMARY KEY (id)
            );

            CREATE INDEX ix_outbox_messages_published_at_occurred_at
                ON outbox_messages (published_at, occurred_at);

            CREATE TABLE processed_events (
                id uuid NOT NULL,
                event_type varchar(200) NOT NULL,
                aggregate_id uuid NOT NULL,
                processed_at timestamp with time zone NOT NULL,
                CONSTRAINT pk_processed_events PRIMARY KEY (id)
            );

            CREATE INDEX ix_processed_events_aggregate_id_processed_at
                ON processed_events (aggregate_id, processed_at);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS processed_events;
            DROP TABLE IF EXISTS outbox_messages;
            """);
    }
}
