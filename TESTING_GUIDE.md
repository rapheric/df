# Testing Guide - Search Endpoints

## Overview
This guide helps you test the newly fixed search endpoints for both DCL and Customer searches in the Deferral Form.

## Step 1: Ensure Backend is Running

```bash
cd c:\Users\Eric.Mewa\kambitoz
dotnet run
```

Wait until you see:
```
✅ Starting app...
Now listening on: https://localhost:5001
```

## Step 2: Get Authentication Token

### Option A: Login via API
```bash
curl -X POST https://localhost:5001/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{
    "email": "admin@ncba.com",
    "password": "Password@123"
  }'
```

Response will include a `token` field. Copy it for use in subsequent requests.

### Option B: Use Frontend
1. Navigate to login page
2. Enter: admin@ncba.com / Password@123
3. Open browser DevTools → Application → Local Storage
4. Find `token` value

## Step 3: Prepare Test Data

### Option A: Using SQL Script (Recommended)
```bash
# Connect to MySQL and run the test data script
mysql -u root -p ncba_dcl < TEST_DATA.sql
```

### Option B: Manual Insertion
1. Insert test customers into Users table
2. Insert test checklists into Checklists table
3. Insert test deferrals into Deferrals table
(See TEST_DATA.sql for full SQL commands)

## Step 4: Test API Endpoints Directly

### Test 1: Search Customers by Number

```bash
# Replace YOUR_TOKEN with the token from Step 2
curl -X GET "https://localhost:5001/api/customers/search?customerNumber=123&loanType=Personal" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json"
```

**Expected Response:**
```json
[
  {
    "id": "f47ac10b-58cc-4372-a567-0e02b2c3d479",
    "customerNumber": "123456",
    "customerName": "Test Customer 1",
    "name": "Test Customer 1",
    "email": "customer1@test.com",
    "active": true,
    "loanType": "Personal"
  }
]
```

**Test Cases:**
- `customerNumber=123` → Should find "123456" (partial match)
- `customerNumber=789` → Should find "789012" (partial match)
- `customerNumber=999` → Should return [] (no match)

### Test 2: Search Deferrals by DCL Number

```bash
curl -X GET "https://localhost:5001/api/deferrals/search?dclNumber=DCL-26" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json"
```

**Expected Response:**
```json
[
  {
    "id": "b47ac10b-58cc-4372-a567-0e02b2c3d001",
    "deferralNumber": "DEF-26-0001",
    "dclNumber": "DCL-26-0001",
    "dclNo": "DCL-26-0001",
    "customerName": "Test Customer 1",
    "customerNumber": "123456",
    "loanType": "Personal",
    "status": "Pending",
    "createdAt": "2026-03-16T..."
  }
]
```

**Test Cases:**
- `dclNumber=DCL` → Should find all checklists starting with "DCL"
- `dclNumber=26` → Should find "DCL-26-0001", "DCL-26-0002"
- `dclNumber=0001` → Should find "DCL-26-0001" (partial match)
- `dclNumber=0999` → Should return [] (no match)

### Test 3: Search Deferrals by Deferral Number

```bash
curl -X GET "https://localhost:5001/api/deferrals/search?deferralNumber=DEF-26" \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json"
```

**Test Cases:**
- `deferralNumber=DEF` → Should find all deferrals
- `deferralNumber=26` → Should find "DEF-26-0001", "DEF-26-0002"
- `deferralNumber=0001` → Should find matching defferals

## Step 5: Test via Frontend UI

### Search by Customer Number
1. Navigate to `/deferrals/form` (or similar route)
2. Click "Search Customer" button
3. Ensure "Search by Customer Number" tab is selected
4. Enter partial customer number: `123`
5. Select loan type: `Personal`
6. Wait 500ms for debounce
7. **Verify**: Dropdown shows matching customers
8. Click on result
9. **Verify**: Form appears with customer data pre-populated

### Search by DCL Number
1. Navigate to `/deferrals/form`
2. Click "Search Customer" button
3. Click "Search by DCL Number" tab
4. Enter partial DCL number: `DCL-26` or just `26`
5. Wait 500ms for debounce
6. **Verify**: Dropdown shows matching deferrals
7. Click on result
8. **Verify**: Form appears with deferral data pre-populated

## Troubleshooting

### Issue: No results returned
- **Check**: Database has test data (run SELECT queries from TEST_DATA.sql)
- **Check**: Token is valid (should not get 401 response)
- **Check**: Search term matches what's in database (exact field names matter)

### Issue: 401 Unauthorized
- **Solution**: Get new token from login endpoint
- **Check**: Token is passed in Authorization header as `Bearer <token>`

### Issue: 400 Bad Request
- **Check**: Query parameters are properly URL encoded
- **Check**: Parameter names match exactly: `customerNumber`, `dclNumber`, `loanType`

### Issue: 500 Internal Server Error
- **Check**: Backend logs for error messages
- **Solution**: Verify foreign key relationships (e.g., CreatedById exists in Users table)

### Issue: Form doesn't populate after selection
- **Check**: Selected data is being passed correctly (browser DevTools)
- **Check**: Form component is receiving the selected customer/deferral data
- **Solution**: Check [src/pages/deferrals/DeferralForm/index.jsx](../../dcldefbm/src/pages/deferrals/DeferralForm/index.jsx) to verify state handling

## Validation Checklist

- ✅ API endpoint `/api/deferrals/search` returns deferrals with correct fields
- ✅ API endpoint `/api/customers/search` returns customers with correct fields
- ✅ Partial search works (e.g., "26" finds "DCL-26-0001")
- ✅ User can select result from dropdown
- ✅ Form displays after selection
- ✅ Selected data is populated in form fields
- ✅ Navigation works correctly
- ✅ Error handling works (invalid token, no results, etc.)

## Performance Considerations

- Search results limited to 20 items per request
- Debounce on frontend is 500ms (prevents excessive API calls)
- Both endpoints use `.Contains()` for flexible partial matching
- Results load in < 1 second for typical queries

## Next Steps

After confirming all tests pass:
1. Test with real production data
2. Add more test cases as needed
3. Monitor API logs for any issues
4. Consider adding advanced filters (date range, status, etc.)
