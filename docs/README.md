# Documentation

Start with the [project overview](../README.md) and [getting started](getting-started.md) to run a clean checkout. No private development workspace or configured ERP is required.

| Goal | Read |
| --- | --- |
| Understand the implementation and its boundaries | [Architecture](architecture.md), [project status](project-status.md) |
| Understand identity, calculations and purchase decisions | [Business rules](business-rules.md) |
| Configure PDF recognition and extraction | [Automatic PDF import](automatic-pdf-import.md) |
| Map spreadsheets and product codes | [Spreadsheet import](excel-import.md) |
| Combine documents during order creation | [Multiple source documents](multiple-source-documents.md) |
| Connect another external catalog or ERP | [ERP integration](erp-integration.md) |
| Implement order reads, writes and recovery | [Purchase order contracts](purchase-order-scripts.md) |
| Map financial data and integration policies | [Financial capture and policies](financial-capture-and-policies.md) |
| Adapt the code with a contributor or coding agent | [Customization](customization.md), [contributing](../CONTRIBUTING.md), [agent directives](../AGENTS.md) |
| Reproduce verification and understand its limits | [Testing](testing.md) |
| Understand generated badges, releases and client updates | [Releases and updates](releases-and-updates.md) |
| Handle credentials and sensitive material | [Security](../SECURITY.md) |

These documents describe the current source and public synthetic verification. Installation-specific schemas, scripts, credentials and business documents are not part of the public contract. Existing serialized names and result aliases are compatibility interfaces, not requirements for the remote system's table names.
