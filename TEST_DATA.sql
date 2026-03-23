-- Test Data Script for Search Endpoints
-- This script populates sample data for testing the new search endpoints

-- 1. Add test customers
INSERT INTO Users (Id, Name, Email, Password, Role, CustomerNumber, Active, CreatedAt, UpdatedAt)
VALUES 
    (
        UNHEX(REPLACE('f47ac10b-58cc-4372-a567-0e02b2c3d479', '-', '')),
        'Test Customer 1',
        'customer1@test.com',
        -- BCrypt hash of "TestPassword123" with rounds=10
        '$2a$10$abc123def456ghi789jklmn',
        'Customer',
        '123456',
        1,
        NOW(),
        NOW()
    ),
    (
        UNHEX(REPLACE('f47ac10b-58cc-4372-a567-0e02b2c3d480', '-', '')),
        'Test Customer 2',
        'customer2@test.com',
        '$2a$10$abc123def456ghi789klmno',
        'Customer',
        '789012',
        1,
        NOW(),
        NOW()
    );

-- 2. Add test checklists (for DCL search)
INSERT INTO Checklists (Id, DclNo, CustomerName, CustomerNumber, LoanType, Status, CreatedAt, UpdatedAt)
VALUES
    (
        UNHEX(REPLACE('a47ac10b-58cc-4372-a567-0e02b2c3d001', '-', '')),
        'DCL-26-0001',
        'Test Customer 1',
        '123456',
        'Personal',
        0,  -- Assuming 0 = Pending status
        NOW(),
        NOW()
    ),
    (
        UNHEX(REPLACE('a47ac10b-58cc-4372-a567-0e02b2c3d002', '-', '')),
        'DCL-26-0002',
        'Test Customer 2',
        '789012',
        'Business',
        0,
        NOW(),
        NOW()
    );

-- 3. Add test deferrals (for deferral search)
INSERT INTO Deferrals (Id, DeferralNumber, DclNo, LoanType, Status, CreatedById, CreatedAt, UpdatedAt)
VALUES
    (
        UNHEX(REPLACE('b47ac10b-58cc-4372-a567-0e02b2c3d001', '-', '')),
        'DEF-26-0001',
        'DCL-26-0001',
        'Personal',
        0,  -- Assuming 0 = Pending
        UNHEX(REPLACE('f47ac10b-58cc-4372-a567-0e02b2c3d479', '-', '')),
        NOW(),
        NOW()
    ),
    (
        UNHEX(REPLACE('b47ac10b-58cc-4372-a567-0e02b2c3d002', '-', '')),
        'DEF-26-0002',
        'DCL-26-0002',
        'Business',
        0,
        UNHEX(REPLACE('f47ac10b-58cc-4372-a567-0e02b2c3d480', '-', '')),
        NOW(),
        NOW()
    );

-- VERIFICATION QUERIES (to check if data was inserted correctly)
SELECT '=== TEST DATA INSERTED ===' as message;
SELECT COUNT(*) as customer_count FROM Users WHERE Role = 'Customer';
SELECT COUNT(*) as checklist_count FROM Checklists;
SELECT COUNT(*) as deferral_count FROM Deferrals;

-- Test specific searches
SELECT '=== Testing DCL Search (partial match on "26") ===' as test_name;
SELECT DclNo, CustomerName, CustomerNumber, LoanType FROM Checklists WHERE DclNo LIKE '%26%';

SELECT '=== Testing Customer Search (number 123456) ===' as test_name;
SELECT Name, CustomerNumber, Email FROM Users WHERE Role = 'Customer' AND CustomerNumber LIKE '%123456%';

SELECT '=== Testing Deferral Search (partial match on "DEF") ===' as test_name;
SELECT DeferralNumber, DclNo, LoanType FROM Deferrals WHERE DeferralNumber LIKE '%DEF%';
