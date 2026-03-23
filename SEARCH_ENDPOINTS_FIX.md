# Search Endpoints Fix - Summary

## Problem Statement
The search by customer and search by DCL number were not working properly. Users couldn't search with partial input and see existing data from the database.

## Root Causes Identified
1. **Missing Backend Endpoint**: The `/api/deferrals/search` endpoint didn't exist
2. **API Call Mismatch**: Frontend was calling correct endpoints but backend in some cases didn't support query parameters

## Solutions Applied

### Backend Changes (kambitoz)

#### 1. Added Search Endpoint to DeferralController.cs
**Endpoint**: `GET /api/deferrals/search`
- **Location**: Lines 580-619 (before DEFERRAL NUMBER GENERATION section)
- **Query Parameters**:
  - `dclNumber` (optional): Search by DCL number (partial match supported)
  - `deferralNumber` (optional): Search by deferral number (partial match supported)
- **Response**: Array of matching deferrals with fields:
  ```json
  {
    "id": "GUID",
    "deferralNumber": "DEF-26-0001",
    "dclNumber": "DCL-26-0183",
    "dclNo": "DCL-26-0183",
    "customerName": "John Doe",
    "customerNumber": "123456",
    "loanType": "Personal",
    "status": "Pending",
    "createdAt": "2026-03-16T..."
  }
  ```
- **Features**:
  - Uses `.Contains()` for partial search (e.g., typing "26" will find "DCL-26-0183")
  - Includes `CreatedBy` user data (loaded via `.Include()`)
  - Limited to 20 results for performance
  - Proper error handling with logging

#### 2. Added GET Customer Search Endpoint to CustomerController.cs  
**Endpoint**: `GET /api/customers/search`
- **Location**: Lines 37-87 (new endpoint added)
- **Query Parameters**:
  - `customerNumber` (optional): Search by customer number
  - `loanType` (optional): Included in response for filtering
- **Response**: Array of matching customers:
  ```json
  {
    "id": "GUID",
    "customerNumber": "123456",
    "customerName": "John Doe",
    "name": "John Doe",
    "email": "john@example.com",
    "active": true,
    "loanType": "Personal"
  }
  ```
- **Notes**:
  - Complements existing `POST /api/customers/search` endpoint
  - Filters by `UserRole.Customer` only
  - Limited to 20 results

### Frontend (No Changes Required)
The frontend API calls in `dcldefbm/src/service/deferralApi.js` were already correct:
- ✅ `searchDeferralByNumber()` → calls `/api/deferrals/search?dclNumber=...`
- ✅ `searchCustomerByNumber()` → calls `/api/customers/search?customerNumber=...&loanType=...`

## How It Works Now

### Search by DCL Number Flow:
1. User clicks "Search Customer" button in DeferralForm
2. Selects "Search by DCL Number" tab
3. Types DCL number (e.g., "DCL-26" or just "26")
4. Frontend calls: `GET /api/deferrals/search?dclNumber=26`
5. Backend returns matching deferrals (supports partial search)
6. User clicks result from dropdown
7. Form pre-populates with selected deferral data
8. User sees the deferral form page

### Search by Customer Number Flow:
1. User clicks "Search Customer" button in DeferralForm
2. Selects "Search by Customer Number" tab (default)
3. Enters customer number (digits only) and selects loan type
4. Frontend calls: `GET /api/customers/search?customerNumber=123&loanType=Personal`
5. Backend returns matching customers
6. User clicks result from dropdown
7. Form pre-populates with customer data
8. User sees the deferral form page

## Field Name Compatibility

### DCL Search Response
Frontend expects → Backend returns:
- `dcl.id` / `dcl._id` → `id` ✅
- `dcl.dclNumber` / `dcl.dclNo` → `dclNumber` + `dclNo` ✅
- `dcl.customerNumber` → `customerNumber` ✅
- `dcl.customerName` → `customerName` ✅
- `dcl.loanType` → `loanType` ✅

### Customer Search Response
Frontend expects → Backend returns:
- `c.id` / `c._id` → `id` ✅
- `c.customerNumber` → `customerNumber` ✅
- `c.name` / `c.customerName` → `name` + `customerName` ✅
- `c.email` → `email` ✅

## Testing Instructions

### Prerequisites
1. Start the .NET backend: `dotnet run` in kambitoz folder
2. Ensure MySQL database is running and accessible
3. Database should have:
   - Checklists with `DclNo` and `CustomerName` fields
   - Deferrals with `DclNo`, `DeferralNumber`, and `LoanType` fields
   - Users with `Role = 'Customer'` for customer search

### Test DCL Search
```bash
# Direct API call
curl -X GET "https://localhost:5001/api/deferrals/search?dclNumber=DCL-26" \
  -H "Authorization: Bearer YOUR_TOKEN"

# Expected response: Array of deferrals matching "DCL-26"
```

### Test Customer Search
```bash
# Direct API call
curl -X GET "https://localhost:5001/api/customers/search?customerNumber=123&loanType=Personal" \
  -H "Authorization: Bearer YOUR_TOKEN"

# Expected response: Array of customers matching "123"
```

### Test via Frontend
1. Navigate to Deferrals form page
2. Click "Search Customer" button
3. Type search term (partial text is supported)
4. Wait 500ms (debounce) for dropdown results
5. Click a result to select it
6. Form should show and pre-populate with selected data

## Changes Applied
- ✅ `Controllers/DeferralController.cs` - Added search endpoint (lines 580-619)
- ✅ `Controllers/CustomerController.cs` - Added GET search endpoint (lines 37-87)
- ✅ Frontend API calls already correct - no changes needed

## Notes
- All searches support partial/contains matching (not just exact matches)
- Debounce on frontend is 500ms (to avoid excessive API calls)
- Results limited to 20 items per search for performance
- All endpoints require authentication (JWT token)
- Error handling implemented with proper logging
