-- Regression checks for production GL Account master.
-- Safe to run repeatedly. All test data is rolled back.
--
-- Usage:
--   psql -h 127.0.0.1 -p 5432 -U <user> -d <db> -v ON_ERROR_STOP=1 -f database/gl_accounts_regression_checks.sql

BEGIN;

DO $$
DECLARE
    v_company_1 BIGINT;
    v_company_2 BIGINT;
    v_location_1 BIGINT;
    v_root_id BIGINT;
    v_child_id BIGINT;
    v_posting_id BIGINT;
    v_non_posting_id BIGINT;
    v_inactive_id BIGINT;
    v_cc_required_id BIGINT;
    v_header_id BIGINT;
    v_division_cost_center_id BIGINT;
    v_block_cost_center_id BIGINT;
    v_level INT;
    v_path TEXT;
    v_expected_error BOOLEAN;
BEGIN
    SELECT id
    INTO v_company_1
    FROM org_companies
    ORDER BY id
    LIMIT 1;

    IF v_company_1 IS NULL THEN
        RAISE EXCEPTION 'CHECK FAILED: org_companies has no data.';
    END IF;

    SELECT id
    INTO v_location_1
    FROM org_locations
    WHERE company_id = v_company_1
    ORDER BY id
    LIMIT 1;

    IF v_location_1 IS NULL THEN
        RAISE EXCEPTION 'CHECK FAILED: org_locations has no row for company_id=%', v_company_1;
    END IF;

    INSERT INTO org_companies (code, name, is_active)
    VALUES ('TSTCO2', 'Test Company 2', TRUE)
    ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name
    RETURNING id INTO v_company_2;

    -- 1) Unique (company_id, account_code) must be enforced.
    INSERT INTO gl_accounts (
        company_id, account_code, account_name, account_type, normal_balance,
        is_posting, is_active, created_by, updated_by
    )
    VALUES (
        v_company_1, 'TU.10001.001', 'Unique Test 1', 'ASSET', 'D',
        TRUE, TRUE, 'REGRESSION', 'REGRESSION'
    );

    v_expected_error := FALSE;
    BEGIN
        INSERT INTO gl_accounts (
            company_id, account_code, account_name, account_type, normal_balance,
            is_posting, is_active, created_by, updated_by
        )
        VALUES (
            v_company_1, 'TU.10001.001', 'Unique Test 1 Dup', 'ASSET', 'D',
            TRUE, TRUE, 'REGRESSION', 'REGRESSION'
        );
    EXCEPTION
        WHEN unique_violation THEN
            v_expected_error := TRUE;
    END;

    IF NOT v_expected_error THEN
        RAISE EXCEPTION 'CHECK FAILED: unique(company_id, account_code) did not reject duplicate.';
    END IF;

    -- Same code on a different company must be allowed.
    INSERT INTO gl_accounts (
        company_id, account_code, account_name, account_type, normal_balance,
        is_posting, is_active, created_by, updated_by
    )
    VALUES (
        v_company_2, 'TU.10001.001', 'Unique Test 1 Company 2', 'ASSET', 'D',
        TRUE, TRUE, 'REGRESSION', 'REGRESSION'
    );

    -- 2) Hierarchy level/path generation.
    INSERT INTO gl_accounts (
        company_id, account_code, account_name, account_type, normal_balance,
        is_posting, is_active, created_by, updated_by
    )
    VALUES (
        v_company_1, 'TH.10010.000', 'Hierarchy Root', 'ASSET', 'D',
        FALSE, TRUE, 'REGRESSION', 'REGRESSION'
    )
    RETURNING id INTO v_root_id;

    INSERT INTO gl_accounts (
        company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
        is_posting, is_active, created_by, updated_by
    )
    VALUES (
        v_company_1, 'TH.11010.001', 'Hierarchy Child', v_root_id, 'ASSET', 'D',
        TRUE, TRUE, 'REGRESSION', 'REGRESSION'
    )
    RETURNING id INTO v_child_id;

    SELECT account_level, full_path::text
    INTO v_level, v_path
    FROM gl_accounts
    WHERE id = v_child_id;

    IF v_level <> 2 THEN
        RAISE EXCEPTION 'CHECK FAILED: expected child account_level=2, got %', v_level;
    END IF;

    IF v_path NOT LIKE 'TH_10010_000.%' THEN
        RAISE EXCEPTION 'CHECK FAILED: expected child full_path to start with TH_10010_000, got %', v_path;
    END IF;

    -- 3) Parent and child must be in the same company.
    v_expected_error := FALSE;
    BEGIN
        INSERT INTO gl_accounts (
            company_id, account_code, account_name, parent_account_id, account_type, normal_balance,
            is_posting, is_active, created_by, updated_by
        )
        VALUES (
            v_company_2, 'TX.11010.001', 'Cross Company Child', v_root_id, 'ASSET', 'D',
            TRUE, TRUE, 'REGRESSION', 'REGRESSION'
        );
    EXCEPTION
        WHEN OTHERS THEN
            v_expected_error := TRUE;
    END;

    IF NOT v_expected_error THEN
        RAISE EXCEPTION 'CHECK FAILED: cross-company parent-child insert was not rejected.';
    END IF;

    -- 4) Type/normal balance consistency.
    v_expected_error := FALSE;
    BEGIN
        INSERT INTO gl_accounts (
            company_id, account_code, account_name, account_type, normal_balance,
            is_posting, is_active, created_by, updated_by
        )
        VALUES (
            v_company_1, 'TB.10020.001', 'Invalid Balance', 'ASSET', 'C',
            TRUE, TRUE, 'REGRESSION', 'REGRESSION'
        );
    EXCEPTION
        WHEN OTHERS THEN
            v_expected_error := TRUE;
    END;

    IF NOT v_expected_error THEN
        RAISE EXCEPTION 'CHECK FAILED: invalid account_type/normal_balance was not rejected.';
    END IF;

    -- 5) Journal detail must use posting and active account from same company.
    INSERT INTO gl_accounts (
        company_id, account_code, account_name, account_type, normal_balance,
        is_posting, is_active, created_by, updated_by
    )
    VALUES (
        v_company_1, 'TP.10030.001', 'Posting OK', 'ASSET', 'D',
        TRUE, TRUE, 'REGRESSION', 'REGRESSION'
    )
    RETURNING id INTO v_posting_id;

    INSERT INTO gl_accounts (
        company_id, account_code, account_name, account_type, normal_balance,
        is_posting, is_active, created_by, updated_by
    )
    VALUES (
        v_company_1, 'TN.10030.010', 'Non Posting', 'ASSET', 'D',
        FALSE, TRUE, 'REGRESSION', 'REGRESSION'
    )
    RETURNING id INTO v_non_posting_id;

    INSERT INTO gl_accounts (
        company_id, account_code, account_name, account_type, normal_balance,
        is_posting, is_active, created_by, updated_by
    )
    VALUES (
        v_company_1, 'TI.10030.011', 'Inactive Posting', 'ASSET', 'D',
        TRUE, FALSE, 'REGRESSION', 'REGRESSION'
    )
    RETURNING id INTO v_inactive_id;

    INSERT INTO gl_journal_headers (
        company_id, location_id, journal_no, journal_date, reference_no, description,
        status, created_by, created_at, updated_at
    )
    VALUES (
        v_company_1, v_location_1, 'REG-GL-CHK-001', CURRENT_DATE, '', 'Regression GL check',
        'DRAFT', 'REGRESSION', NOW(), NOW()
    )
    RETURNING id INTO v_header_id;

    -- Positive path: posting + active + same company should pass.
    INSERT INTO gl_journal_details (
        header_id, line_no, account_id, description, debit, credit, created_at, updated_at
    )
    VALUES (
        v_header_id, 1, v_posting_id, 'Valid posting account', 100, 0, NOW(), NOW()
    );

    -- Non-posting must fail.
    v_expected_error := FALSE;
    BEGIN
        INSERT INTO gl_journal_details (
            header_id, line_no, account_id, description, debit, credit, created_at, updated_at
        )
        VALUES (
            v_header_id, 2, v_non_posting_id, 'Should fail non-posting', 100, 0, NOW(), NOW()
        );
    EXCEPTION
        WHEN OTHERS THEN
            v_expected_error := TRUE;
    END;
    IF NOT v_expected_error THEN
        RAISE EXCEPTION 'CHECK FAILED: non-posting account was accepted by gl_journal_details.';
    END IF;

    -- Inactive must fail.
    v_expected_error := FALSE;
    BEGIN
        INSERT INTO gl_journal_details (
            header_id, line_no, account_id, description, debit, credit, created_at, updated_at
        )
        VALUES (
            v_header_id, 3, v_inactive_id, 'Should fail inactive', 100, 0, NOW(), NOW()
        );
    EXCEPTION
        WHEN OTHERS THEN
            v_expected_error := TRUE;
    END;
    IF NOT v_expected_error THEN
        RAISE EXCEPTION 'CHECK FAILED: inactive account was accepted by gl_journal_details.';
    END IF;

    -- 6) Account requiring cost center must reject missing/non-posting cost center.
    INSERT INTO gl_accounts (
        company_id, account_code, account_name, account_type, normal_balance,
        is_posting, is_active, requires_cost_center, created_by, updated_by
    )
    VALUES (
        v_company_1, 'TC.50030.001', 'Cost Center Required', 'EXPENSE', 'D',
        TRUE, TRUE, TRUE, 'REGRESSION', 'REGRESSION'
    )
    RETURNING id INTO v_cc_required_id;

    INSERT INTO gl_cost_centers (
        company_id, location_id, cost_center_code, cost_center_name,
        estate_code, estate_name, division_code, division_name, block_code, block_name,
        level, is_posting, is_active, created_by, updated_by
    )
    VALUES
    (
        v_company_1, v_location_1, 'NE-D01', 'Division 01',
        'NE', 'North Estate', 'D01', 'Division 01', '', '',
        'DIVISION', FALSE, TRUE, 'REGRESSION', 'REGRESSION'
    ),
    (
        v_company_1, v_location_1, 'NE-D01-B12', 'Block B12',
        'NE', 'North Estate', 'D01', 'Division 01', 'B12', 'Block B12',
        'BLOCK', TRUE, TRUE, 'REGRESSION', 'REGRESSION'
    );

    SELECT id
    INTO v_division_cost_center_id
    FROM gl_cost_centers
    WHERE company_id = v_company_1
      AND location_id = v_location_1
      AND cost_center_code = 'NE-D01';

    SELECT id
    INTO v_block_cost_center_id
    FROM gl_cost_centers
    WHERE company_id = v_company_1
      AND location_id = v_location_1
      AND cost_center_code = 'NE-D01-B12';

    v_expected_error := FALSE;
    BEGIN
        INSERT INTO gl_journal_details (
            header_id, line_no, account_id, description, debit, credit, created_at, updated_at
        )
        VALUES (
            v_header_id, 4, v_cc_required_id, 'Should fail missing cost center', 50, 0, NOW(), NOW()
        );
    EXCEPTION
        WHEN OTHERS THEN
            v_expected_error := TRUE;
    END;
    IF NOT v_expected_error THEN
        RAISE EXCEPTION 'CHECK FAILED: missing required cost center was accepted by gl_journal_details.';
    END IF;

    v_expected_error := FALSE;
    BEGIN
        INSERT INTO gl_journal_details (
            header_id, line_no, account_id, description, debit, credit, cost_center_id, cost_center_code, created_at, updated_at
        )
        VALUES (
            v_header_id, 5, v_cc_required_id, 'Should fail division cost center', 50, 0, v_division_cost_center_id, 'NE-D01', NOW(), NOW()
        );
    EXCEPTION
        WHEN OTHERS THEN
            v_expected_error := TRUE;
    END;
    IF NOT v_expected_error THEN
        RAISE EXCEPTION 'CHECK FAILED: non-posting cost center was accepted by gl_journal_details.';
    END IF;

    INSERT INTO gl_journal_details (
        header_id, line_no, account_id, description, debit, credit, cost_center_id, cost_center_code, created_at, updated_at
    )
    VALUES (
        v_header_id, 6, v_cc_required_id, 'Posting cost center', 50, 0, v_block_cost_center_id, 'NE-D01-B12', NOW(), NOW()
    );

    RAISE NOTICE 'GL ACCOUNT REGRESSION CHECKS: PASSED';
END
$$;

ROLLBACK;
