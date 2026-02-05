# Node.js to C# Conversion - Implementation Plan

## Overview
This document outlines the complete conversion plan from Node.js (Express + MongoDB) to C# (.NET 8 + MySQL/SQL Server).

## Current Status

### ✅ Completed Components
1. **Core Models** - User, Checklist, Document, Deferral, UserLog, Notification, Upload
2. **Database Context** - ApplicationDbContext with all relationships configured
3. **Authentication** - JWT token generation, password hashing, login/register endpoints
4. **Base Controllers** - Auth, User, Checklist (partial), Deferral (partial), Audit, RM (partial), Checker (partial)
5. **Helpers** - JWT token generator, password hasher, file upload helper
6. **SignalR** - Hub setup for real-time communication

### ⚠️ Missing/Incomplete Components

#### 1. Models
- **Extension Model** - For deferral extension requests (complete model needs to be created)
- **Customer Search** - Endpoint/service for customer search functionality

#### 2. Controllers  
- **ExtensionController** - Complete controller for extension workflow
- **CustomerController** - Simple controller for customer search

#### 3. Services
- Need to expand or fix:
  - **AdminService** - Missing `Microsoft.EntityFrameworkCore` using directive
  - **AuditLogService** - `AuditLogs` DbSet exists but needs proper using directive
  - **EmailService** - Currently a stub, needs full implementation
  - **ChecklistService** - For complex checklist operations
  - **DeferralService** - For deferral-related business logic
  - **ExtensionService** - For extension workflow logic
  - **NotificationService** - For centralized notification management

#### 4. DTOs (Data Transfer Objects)
Missing DTOs for:
- Extension requests/responses
- Customer search
- Document updates for Checker
- Complex checklist operations

#### 5. Missing Endpoints

**CoCreator Controller** (Partially complete, missing):
- ✅ Create checklist
- ✅ Get all checklists
- ✅ Get by ID/DCL number
- ✅ Update checklist
- ✅ Search customer
- ✅ Get by creator
- ✅ Co-create review
- ✅ Co-checker approval
- ✅ Submit to RM
- ✅ Submit to CoChecker
- ✅ Document operations (add, update, delete)
- ✅ File upload
- ✅ Download checklist
- ✅ Active checklists

**RM Controller** (Partially complete, missing):
- ✅ Get my queue
- ✅ Submit to CoCreator
- ✅ Get completed DCLs
- ✅ Delete DCL
- ✅ Get by ID
- ✅ Delete document file
- ✅ Get notifications
- ✅ Mark notification as read
- ❌ Upload supporting documents (exists in Node.js)

**Checker Controller** (Partially complete, missing):
- ✅ Get active DCLs
- ✅ Get my queue
- ✅ Get completed DCLs
- ✅ Get DCL by ID
- ✅ Update DCL status
- ✅ Auto-moved queue
- ✅ Update status
- ✅ Get reports
- ✅ Approve DCL
- ✅ Reject DCL
- ❌ DocumentUpdates property in UpdateCheckerDCLRequest DTO (causing build error)

**Extension Controller** (COMPLETELY MISSING):
- ❌ Create extension request
- ❌ Get my extensions (RM)
- ❌ Get approver extensions
- ❌ Get approver actioned extensions
- ❌ Approve extension
- ❌ Reject extension
- ❌ Approve as creator
- ❌ Reject as creator
- ❌ Approve as checker
- ❌ Reject as checker
- ❌ Get creator pending extensions
- ❌ Get checker pending extensions
- ❌ Get extension by ID

**Customer Controller** (COMPLETELY MISSING):
- ❌ Search customer
- ❌ Search by DCL

## Build Errors to Fix

1. **AdminService.cs** - Missing `using Microsoft.EntityFrameworkCore;` directive
2. **AuditLogService.cs** - `AuditLogs` DbSet reference issue (already exists in DbContext, just needs using directive)
3. **CheckerController.cs** - Missing `DocumentUpdates` property in `UpdateCheckerDCLRequest` DTO

## Implementation Steps

### Step 1: Fix Build Errors (HIGH PRIORITY)
1. Add missing using directives to Services
2. Add `DocumentUpdates` property to `UpdateCheckerDCLRequest` DTO
3. Verify build succeeds

### Step 2: Create Missing Models (HIGH PRIORITY)
1. Create `Extension.cs` model with all properties
2. Add `DbSet<Extension>` to ApplicationDbContext
3. Configure Extension entity relationships
4. Create migration for Extension table

### Step 3: Create Missing DTOs (HIGH PRIORITY)
1. **ExtensionDTOs.cs** - All request/response DTOs for extensions
2. **CustomerDTOs.cs** - Customer search DTOs
3. Update **CheckerDTOs.cs** - Add DocumentUpdates property
4. **ChecklistDTOs.cs** - Add any missing complex operation DTOs

### Step 4: Implement Services (MEDIUM PRIORITY)
1. **EmailService** - Implement actual email sending (using SMTP or SendGrid)
2. **ExtensionService** - Business logic for extension workflow
3. **NotificationService** - Centralized notification creation
4. **ChecklistService** - Complex checklist business logic
5. **DeferralService** - Deferral workflow logic

### Step 5: Create Missing Controllers (HIGH PRIORITY)
1. **ExtensionController** - Complete implementation with all endpoints
2. **CustomerController** - Simple search endpoints
3. Complete missing endpoints in existing controllers

### Step 6: Complete Existing Controllers (MEDIUM PRIORITY)
1. Add upload supporting docs to RMController
2. Verify all checker endpoints work correctly
3. Test all CoCreator endpoints

### Step 7: SignalR Integration (MEDIUM PRIORITY)
1. Implement real-time user tracking (similar to Socket.io in Node.js)
2. Add real-time notification broadcasting
3. Add real-time DCL status updates

### Step 8: Testing & Validation (HIGH PRIORITY)
1. Create integration tests for all endpoints
2. Test against React frontend
3. Verify exact functionality matches Node.js version
4. Load testing for performance comparison

### Step 9: Documentation (LOW PRIORITY)
1. Update API documentation
2. Update README with final endpoint list
3. Create migration guide for frontend developers

## Detailed Endpoint Comparison

### Node.js Routes → C# Routes Mapping

#### Extensions (node: /api/extensions)
| Node.js | C# | Status |
|---------|-----|--------|
| POST / | POST /api/extensions | ❌ Missing |
| GET /my | GET /api/extensions/my | ❌ Missing |
| GET /approver/queue | GET /api/extensions/approver/queue | ❌ Missing |
| GET /approver/actioned | GET /api/extensions/approver/actioned | ❌ Missing |
| PUT /:id/approve | PUT /api/extensions/{id}/approve | ❌ Missing |
| PUT /:id/reject | PUT /api/extensions/{id}/reject | ❌ Missing |
| GET /creator/pending | GET /api/extensions/creator/pending | ❌ Missing |
| PUT /:id/approve-creator | PUT /api/extensions/{id}/approve-creator | ❌ Missing |
| PUT /:id/reject-creator | PUT /api/extensions/{id}/reject-creator | ❌ Missing |
| GET /checker/pending | GET /api/extensions/checker/pending | ❌ Missing |
| PUT /:id/approve-checker | PUT /api/extensions/{id}/approve-checker | ❌ Missing |
| PUT /:id/reject-checker | PUT /api/extensions/{id}/reject-checker | ❌ Missing |
| GET /:id | GET /api/extensions/{id} | ❌ Missing |

#### Customers (node: /api/customers)
| Node.js | C# | Status |
|---------|-----|--------|
| POST /search | POST /api/customers/search | ❌ Missing |
| GET /search-dcl | GET /api/customers/search-dcl | ❌ Missing |

## Testing Strategy

### Unit Tests
- Test each service method independently
- Mock database context
- Verify business logic correctness

### Integration Tests
- Test complete API endpoints
- Use test database
- Verify data persistence and retrieval

### End-to-End Tests
- Test with React frontend
- Verify Socket.IO → SignalR migration
- Test file uploads/downloads
- Verify notifications

## Success Criteria

✅ All build errors resolved
✅ All Node.js endpoints have C# equivalents
✅ All functionality tested and working
✅ Frontend integration successful (no frontend changes needed)
✅ Performance meets or exceeds Node.js version
✅ All tests passing
✅ Documentation complete

## Timeline Estimate

- **Step 1** (Fix Build Errors): 15 minutes ⚡
- **Step 2** (Missing Models): 30 minutes ⚡
- **Step 3** (Missing DTOs): 45 minutes ⚡
- **Step 4** (Services): 2 hours 
- **Step 5** (Missing Controllers): 3 hours ⚡
- **Step 6** (Complete Controllers): 1 hour
- **Step 7** (SignalR): 2 hours
- **Step 8** (Testing): 4 hours ⚡
- **Step 9** (Documentation): 1 hour

**Total Estimated Time**: ~14-15 hours of focused development

⚡ = Critical path items that need immediate attention
