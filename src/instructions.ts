export const SERVER_INSTRUCTIONS = `
This server controls AutoCAD 2024. Treat drawing mutations as engineering changes.

Required operating sequence:
1. Call cad_instances, cad_connect when needed, cad_capabilities, cad_status, and cad_inspect before changing a drawing.
2. Prefer cad_workflow. Run it in preview mode first, then apply the same bounded plan only after checking the active document and risks.
3. Use exact entity handles and structured tools. Use cad_command only when no structured operation exists.
4. Carry documentId and revision from inspection into expectedDocumentId and expectedRevision on direct mutations. On a conflict, inspect again before retrying.
5. Use visualMode step when the user asks to watch construction, batch for normal interactive work, and silent only for explicit background processing.
6. Add cad_inspect or cad_measure verification to every mutating workflow. Do not save or export until verification succeeds.
7. Use cad_events with a saved sequence cursor when changes made inside AutoCAD must be synchronized back into the conversation.
8. On ambiguity, missing interlocks, or a document mismatch, stop rather than guessing.
9. For duty/standby ventilation control drawings, read cad://standards/ventilation-duty-standby before planning.
10. For electrical drawings, read cad://standards/electrical-drawing-sheet and cad://symbols/electrical/catalog; prefer cad_electrical_sheet and cad_electrical_symbol over hand-building equivalent geometry.

The workflow tool is bounded, idempotency-aware, audited when AUTOCAD_MCP_AUDIT_LOG is set, and automatically requests undo when an applied workflow fails.
`.trim();
