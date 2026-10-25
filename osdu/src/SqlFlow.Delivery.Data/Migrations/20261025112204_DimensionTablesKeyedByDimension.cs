using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DimensionTablesKeyedByDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Each table is keyed, and so clustered, on its partition, its dimension and the row's number. Its secondary
            // indexes are dropped first and made again last, so each is built once, on the key it ends with: changing the
            // clustered key while they stand would rebuild each of them for the heap between, and again for the new key.

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttributeName_AttributeId",
                schema: "osdu",
                table: "DimensionAttributeName");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttributeName_PartitionId_DimensionId_Name",
                schema: "osdu",
                table: "DimensionAttributeName");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttributeName",
                schema: "osdu",
                table: "DimensionAttributeName");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttributeName",
                schema: "osdu",
                table: "DimensionAttributeName",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttributeName_AttributeId",
                schema: "osdu",
                table: "DimensionAttributeName",
                columns: new[] { "AttributeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttributeName_PartitionId_DimensionId_Name",
                schema: "osdu",
                table: "DimensionAttributeName",
                columns: new[] { "PartitionId", "DimensionId", "Name" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_DimensionRun_DimensionRunId",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRun_PartitionId_DimensionId_StartedUtc",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRun_RunId",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionRun",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionRun",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "PartitionId", "DimensionId", "DimensionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_DimensionRunId",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "DimensionRunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_PartitionId_DimensionId_StartedUtc",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "PartitionId", "DimensionId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_RunId",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "RunId" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionChange_ChangeId",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropIndex(
                name: "IX_DimensionChange_PartitionId_DimensionId_ValueId_ChangeId",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropIndex(
                name: "IX_DimensionChange_PartitionId_DimensionRunId",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionChange",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionChange",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "DimensionId", "ChangeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_ChangeId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "ChangeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_PartitionId_DimensionId_ValueId_ChangeId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "ChangeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_PartitionId_DimensionRunId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "DimensionRunId" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_MemberId",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Value",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_RemovedRunId",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Records_Value",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionMember",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionMember",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_MemberId",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "MemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Value",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_RemovedRunId",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "RemovedRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Records_Value",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "Records", "Value" },
                descending: new[] { false, false, true, false });

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_ValueId",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_OriginalHash",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_MemberId",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_Count_ValueId",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionValue",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionValue",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "ValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_OriginalHash",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "OriginalHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_MemberId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_Count_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "Count", "ValueId" },
                descending: new[] { false, false, true, false });

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_TextId",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "TextId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_TextId",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "TextId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "TextHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "Value" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_KeyRecordId",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_EntityType",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionKeyRecord",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionKeyRecord",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "KeyRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_KeyRecordId",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "KeyRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "RecordHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_EntityType",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "EntityType" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_DimensionRecordId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionRecord",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionRecord",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "DimensionRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_DimensionRecordId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "DimensionRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_ValueId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "AttributeValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "AttributeValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_ValueId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "AttributeId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "Value" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionElement_ElementValueId",
                schema: "osdu",
                table: "DimensionElement");

            migrationBuilder.DropIndex(
                name: "IX_DimensionElement_PartitionId_DimensionId_ValueId_Seq_AttributeId",
                schema: "osdu",
                table: "DimensionElement");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionElement",
                schema: "osdu",
                table: "DimensionElement");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionElement",
                schema: "osdu",
                table: "DimensionElement",
                columns: new[] { "PartitionId", "DimensionId", "ElementValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionElement_ElementValueId",
                schema: "osdu",
                table: "DimensionElement",
                columns: new[] { "ElementValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionElement_PartitionId_DimensionId_ValueId_Seq_AttributeId",
                schema: "osdu",
                table: "DimensionElement",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "Seq", "AttributeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The same order back, so the secondary indexes are built once, on the partition and the row's number again.

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttributeName_AttributeId",
                schema: "osdu",
                table: "DimensionAttributeName");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttributeName_PartitionId_DimensionId_Name",
                schema: "osdu",
                table: "DimensionAttributeName");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttributeName",
                schema: "osdu",
                table: "DimensionAttributeName");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttributeName",
                schema: "osdu",
                table: "DimensionAttributeName",
                columns: new[] { "PartitionId", "AttributeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttributeName_AttributeId",
                schema: "osdu",
                table: "DimensionAttributeName",
                columns: new[] { "AttributeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttributeName_PartitionId_DimensionId_Name",
                schema: "osdu",
                table: "DimensionAttributeName",
                columns: new[] { "PartitionId", "DimensionId", "Name" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_DimensionRun_DimensionRunId",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRun_PartitionId_DimensionId_StartedUtc",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRun_RunId",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionRun",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionRun",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "PartitionId", "DimensionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_DimensionRunId",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "DimensionRunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_PartitionId_DimensionId_StartedUtc",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "PartitionId", "DimensionId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRun_RunId",
                schema: "osdu",
                table: "DimensionRun",
                columns: new[] { "RunId" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionChange_ChangeId",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropIndex(
                name: "IX_DimensionChange_PartitionId_DimensionId_ValueId_ChangeId",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropIndex(
                name: "IX_DimensionChange_PartitionId_DimensionRunId",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionChange",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionChange",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "ChangeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_ChangeId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "ChangeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_PartitionId_DimensionId_ValueId_ChangeId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "ChangeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionChange_PartitionId_DimensionRunId",
                schema: "osdu",
                table: "DimensionChange",
                columns: new[] { "PartitionId", "DimensionRunId" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_MemberId",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Value",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_RemovedRunId",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Records_Value",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionMember",
                schema: "osdu",
                table: "DimensionMember");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionMember",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_MemberId",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "MemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Value",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_RemovedRunId",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "RemovedRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionMember_PartitionId_DimensionId_Records_Value",
                schema: "osdu",
                table: "DimensionMember",
                columns: new[] { "PartitionId", "DimensionId", "Records", "Value" },
                descending: new[] { false, false, true, false });

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_ValueId",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_OriginalHash",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_MemberId",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_Count_ValueId",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionValue",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionValue",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "ValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_OriginalHash",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "OriginalHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_MemberId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_Count_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "Count", "ValueId" },
                descending: new[] { false, false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "ValueId" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_TextId",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "TextId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_TextId",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "TextId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "TextHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "Value" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_KeyRecordId",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_EntityType",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionKeyRecord",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionKeyRecord",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "KeyRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_KeyRecordId",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "KeyRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "RecordHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_EntityType",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "EntityType" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_DimensionRecordId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionRecord",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionRecord",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_DimensionRecordId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "DimensionRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_ValueId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "AttributeValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "AttributeValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_ValueId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "AttributeId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "Value" });

            migrationBuilder.DropIndex(
                name: "IX_DimensionElement_ElementValueId",
                schema: "osdu",
                table: "DimensionElement");

            migrationBuilder.DropIndex(
                name: "IX_DimensionElement_PartitionId_DimensionId_ValueId_Seq_AttributeId",
                schema: "osdu",
                table: "DimensionElement");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionElement",
                schema: "osdu",
                table: "DimensionElement");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionElement",
                schema: "osdu",
                table: "DimensionElement",
                columns: new[] { "PartitionId", "ElementValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionElement_ElementValueId",
                schema: "osdu",
                table: "DimensionElement",
                columns: new[] { "ElementValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionElement_PartitionId_DimensionId_ValueId_Seq_AttributeId",
                schema: "osdu",
                table: "DimensionElement",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "Seq", "AttributeId" },
                unique: true);
        }
    }
}
