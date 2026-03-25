-- Bulk assign real UAT users to seeded GL/Inventory roles.
-- Usage:
-- 1) Edit the VALUES section inside tmp_uat_user_assignment.
-- 2) Run:
--    psql -h <host> -p <port> -U <user> -d <db> -v ON_ERROR_STOP=1 -f database/assign_uat_users.sql
--
-- Notes:
-- - This script enforces 1 user = 1 role (it replaces existing sec_user_roles rows).
-- - It supports multiple locations per user (repeat username on multiple rows).
-- - Set exactly one row with is_default_location=TRUE per user; if omitted, first location is used.
-- - It sets user company/location access and default company/location from the same input rows.
-- - If password_hash is NULL, existing password is kept.
-- - If user does not exist, user is created with a placeholder hash unless password_hash provided.

BEGIN;

CREATE TEMP TABLE tmp_uat_user_assignment (
    username            VARCHAR(100) NOT NULL,
    full_name           VARCHAR(160) NOT NULL,
    email               VARCHAR(255),
    role_code           VARCHAR(80)  NOT NULL,
    company_code        VARCHAR(80)  NOT NULL,
    location_code       VARCHAR(80)  NOT NULL,
    is_default_location BOOLEAN      NOT NULL DEFAULT FALSE,
    is_active           BOOLEAN      NOT NULL DEFAULT TRUE,
    password_hash       TEXT         NULL
) ON COMMIT DROP;

-- Fill real UAT users here (replace sample rows).
-- Repeat username for additional allowed locations.
INSERT INTO tmp_uat_user_assignment (
    username, full_name, email, role_code, company_code, location_code, is_default_location, is_active, password_hash
)
VALUES
    ('uat_gl_staff_01', 'UAT GL Staff 01', 'uat.gl.staff.01@local', 'GL_STAFF', 'AGRINOVA', 'HO', TRUE, TRUE, NULL),
    ('uat_gl_staff_01', 'UAT GL Staff 01', 'uat.gl.staff.01@local', 'GL_STAFF', 'AGRINOVA', 'KB', FALSE, TRUE, NULL),
    ('uat_gl_approver_01', 'UAT GL Approver 01', 'uat.gl.approver.01@local', 'GL_APPROVER', 'AGRINOVA', 'HO', TRUE, TRUE, NULL),
    ('uat_inv_staff_01', 'UAT INV Staff 01', 'uat.inv.staff.01@local', 'INV_STAFF', 'AGRINOVA', 'HO', TRUE, TRUE, NULL),
    ('uat_inv_staff_01', 'UAT INV Staff 01', 'uat.inv.staff.01@local', 'INV_STAFF', 'AGRINOVA', 'PK', FALSE, TRUE, NULL),
    ('uat_inv_approver_01', 'UAT INV Approver 01', 'uat.inv.approver.01@local', 'INV_APPROVER', 'AGRINOVA', 'HO', TRUE, TRUE, NULL);

DO $$
DECLARE
    rec_user RECORD;
    rec_loc RECORD;

    v_user_id BIGINT;
    v_role_id BIGINT;
    v_company_id BIGINT;
    v_location_id BIGINT;
    v_default_location_id BIGINT;

    v_role_code TEXT;
    v_company_code TEXT;

    v_distinct_role_count INTEGER;
    v_distinct_company_count INTEGER;
    v_distinct_full_name_count INTEGER;
    v_distinct_is_active_count INTEGER;
    v_distinct_email_count INTEGER;
    v_distinct_password_count INTEGER;
    v_default_row_count INTEGER;
    v_location_row_count INTEGER;

    v_default_hash TEXT := 'pbkdf2-sha256$120000$853OQt0HZkk/RLFJP2ZZGA==$w7+D8I+s3xKcOFhMJ6ZNlO3Om2JN3kXR2JzqhB5rC1U=';
BEGIN
    FOR rec_user IN
        SELECT
            lower(trim(t.username)) AS username,
            max(trim(t.full_name)) AS full_name,
            max(NULLIF(trim(COALESCE(t.email, '')), '')) AS email,
            bool_and(t.is_active) AS is_active,
            max(NULLIF(trim(COALESCE(t.password_hash, '')), '')) AS password_hash
        FROM tmp_uat_user_assignment t
        GROUP BY lower(trim(t.username))
    LOOP
        IF rec_user.username IS NULL OR rec_user.username = '' THEN
            RAISE EXCEPTION 'username is required';
        END IF;

        SELECT
            count(DISTINCT upper(trim(t.role_code))),
            count(DISTINCT upper(trim(t.company_code))),
            count(DISTINCT trim(t.full_name)),
            count(DISTINCT t.is_active),
            count(DISTINCT NULLIF(trim(COALESCE(t.email, '')), '')),
            count(DISTINCT NULLIF(trim(COALESCE(t.password_hash, '')), '')),
            COALESCE(sum(CASE WHEN t.is_default_location THEN 1 ELSE 0 END), 0),
            count(*)
        INTO
            v_distinct_role_count,
            v_distinct_company_count,
            v_distinct_full_name_count,
            v_distinct_is_active_count,
            v_distinct_email_count,
            v_distinct_password_count,
            v_default_row_count,
            v_location_row_count
        FROM tmp_uat_user_assignment t
        WHERE lower(trim(t.username)) = rec_user.username;

        IF v_distinct_role_count <> 1 THEN
            RAISE EXCEPTION 'username=% must map to exactly 1 role_code', rec_user.username;
        END IF;

        IF v_distinct_company_count <> 1 THEN
            RAISE EXCEPTION 'username=% must map to exactly 1 company_code', rec_user.username;
        END IF;

        IF v_distinct_full_name_count <> 1 THEN
            RAISE EXCEPTION 'username=% has inconsistent full_name across rows', rec_user.username;
        END IF;

        IF v_distinct_is_active_count <> 1 THEN
            RAISE EXCEPTION 'username=% has inconsistent is_active across rows', rec_user.username;
        END IF;

        IF v_distinct_email_count > 1 THEN
            RAISE EXCEPTION 'username=% has inconsistent email across rows', rec_user.username;
        END IF;

        IF v_distinct_password_count > 1 THEN
            RAISE EXCEPTION 'username=% has inconsistent password_hash across rows', rec_user.username;
        END IF;

        IF v_default_row_count > 1 THEN
            RAISE EXCEPTION 'username=% has more than one default location row', rec_user.username;
        END IF;

        IF v_location_row_count < 1 THEN
            RAISE EXCEPTION 'username=% must have at least one location row', rec_user.username;
        END IF;

        SELECT
            upper(trim(t.role_code)),
            upper(trim(t.company_code))
        INTO v_role_code, v_company_code
        FROM tmp_uat_user_assignment t
        WHERE lower(trim(t.username)) = rec_user.username
        LIMIT 1;

        SELECT r.id
        INTO v_role_id
        FROM sec_roles r
        WHERE upper(r.code) = v_role_code
          AND r.is_active = TRUE
        LIMIT 1;

        IF v_role_id IS NULL THEN
            RAISE EXCEPTION 'role_code not found/active: % (username=%)', v_role_code, rec_user.username;
        END IF;

        SELECT c.id
        INTO v_company_id
        FROM org_companies c
        WHERE upper(c.code) = v_company_code
          AND c.is_active = TRUE
        LIMIT 1;

        IF v_company_id IS NULL THEN
            RAISE EXCEPTION 'company_code not found/active: % (username=%)', v_company_code, rec_user.username;
        END IF;

        INSERT INTO app_users (
            username,
            full_name,
            email,
            password_hash,
            is_active,
            default_company_id,
            default_location_id,
            created_at,
            updated_at
        )
        VALUES (
            rec_user.username,
            rec_user.full_name,
            rec_user.email,
            COALESCE(rec_user.password_hash, v_default_hash),
            rec_user.is_active,
            NULL,
            NULL,
            NOW(),
            NOW()
        )
        ON CONFLICT (username) DO UPDATE
        SET full_name = EXCLUDED.full_name,
            email = EXCLUDED.email,
            is_active = EXCLUDED.is_active,
            updated_at = NOW();

        SELECT u.id
        INTO v_user_id
        FROM app_users u
        WHERE lower(u.username) = rec_user.username
        LIMIT 1;

        IF v_user_id IS NULL THEN
            RAISE EXCEPTION 'failed to resolve user id for username=%', rec_user.username;
        END IF;

        IF rec_user.password_hash IS NOT NULL THEN
            UPDATE app_users
            SET password_hash = rec_user.password_hash,
                updated_at = NOW()
            WHERE id = v_user_id;
        END IF;

        DELETE FROM sec_user_roles WHERE user_id = v_user_id;
        INSERT INTO sec_user_roles (user_id, role_id)
        VALUES (v_user_id, v_role_id)
        ON CONFLICT DO NOTHING;

        DELETE FROM sec_user_company_access WHERE user_id = v_user_id;
        INSERT INTO sec_user_company_access (user_id, company_id)
        VALUES (v_user_id, v_company_id)
        ON CONFLICT DO NOTHING;

        DELETE FROM sec_user_location_access WHERE user_id = v_user_id;
        v_default_location_id := NULL;

        FOR rec_loc IN
            SELECT
                upper(trim(t.location_code)) AS location_code,
                bool_or(t.is_default_location) AS is_default_location
            FROM tmp_uat_user_assignment t
            WHERE lower(trim(t.username)) = rec_user.username
            GROUP BY upper(trim(t.location_code))
            ORDER BY upper(trim(t.location_code))
        LOOP
            IF rec_loc.location_code IS NULL OR rec_loc.location_code = '' THEN
                RAISE EXCEPTION 'username=% contains empty location_code', rec_user.username;
            END IF;

            SELECT l.id
            INTO v_location_id
            FROM org_locations l
            WHERE upper(l.code) = rec_loc.location_code
              AND l.company_id = v_company_id
              AND l.is_active = TRUE
            LIMIT 1;

            IF v_location_id IS NULL THEN
                RAISE EXCEPTION 'location_code not found/active for company: %.% (username=%)', v_company_code, rec_loc.location_code, rec_user.username;
            END IF;

            INSERT INTO sec_user_location_access (user_id, location_id)
            VALUES (v_user_id, v_location_id)
            ON CONFLICT DO NOTHING;

            IF rec_loc.is_default_location THEN
                v_default_location_id := v_location_id;
            END IF;
        END LOOP;

        IF v_default_location_id IS NULL THEN
            SELECT l.id
            INTO v_default_location_id
            FROM org_locations l
            JOIN sec_user_location_access ula ON ula.location_id = l.id
            WHERE ula.user_id = v_user_id
            ORDER BY upper(l.code)
            LIMIT 1;
        END IF;

        IF v_default_location_id IS NULL THEN
            RAISE EXCEPTION 'username=% failed to resolve default location', rec_user.username;
        END IF;

        UPDATE app_users
        SET default_company_id = v_company_id,
            default_location_id = v_default_location_id,
            updated_at = NOW()
        WHERE id = v_user_id;
    END LOOP;
END
$$;

SELECT
    u.username,
    r.code AS role_code,
    c.code AS default_company,
    l.code AS default_location,
    COALESCE(string_agg(DISTINCT la.code, ', ' ORDER BY la.code), '') AS allowed_locations
FROM (
    SELECT DISTINCT lower(trim(t.username)) AS username
    FROM tmp_uat_user_assignment t
) x
JOIN app_users u ON lower(u.username) = x.username
LEFT JOIN sec_user_roles ur ON ur.user_id = u.id
LEFT JOIN sec_roles r ON r.id = ur.role_id
LEFT JOIN org_companies c ON c.id = u.default_company_id
LEFT JOIN org_locations l ON l.id = u.default_location_id
LEFT JOIN sec_user_location_access ula ON ula.user_id = u.id
LEFT JOIN org_locations la ON la.id = ula.location_id
GROUP BY u.username, r.code, c.code, l.code
ORDER BY u.username;

COMMIT;
