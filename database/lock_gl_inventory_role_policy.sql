CREATE TABLE IF NOT EXISTS sec_role_policy_lock (
    role_code VARCHAR(80) PRIMARY KEY,
    is_locked BOOLEAN NOT NULL DEFAULT TRUE,
    locked_reason TEXT NOT NULL DEFAULT '',
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

INSERT INTO sec_role_policy_lock (role_code, is_locked, locked_reason, updated_at)
VALUES
    ('GL_ADMIN', TRUE, 'Managed by DB migration policy', NOW()),
    ('GL_STAFF', TRUE, 'Managed by DB migration policy', NOW()),
    ('GL_APPROVER', TRUE, 'Managed by DB migration policy', NOW()),
    ('INV_ADMIN', TRUE, 'Managed by DB migration policy', NOW()),
    ('INV_STAFF', TRUE, 'Managed by DB migration policy', NOW()),
    ('INV_APPROVER', TRUE, 'Managed by DB migration policy', NOW()),
    ('FINANCE_ADMIN', TRUE, 'Managed by DB migration policy', NOW())
ON CONFLICT (role_code) DO UPDATE
SET is_locked = EXCLUDED.is_locked,
    locked_reason = EXCLUDED.locked_reason,
    updated_at = NOW();

CREATE OR REPLACE FUNCTION fn_is_role_policy_bypass_enabled()
RETURNS BOOLEAN
LANGUAGE plpgsql
AS $$
DECLARE
    v_raw TEXT;
BEGIN
    v_raw := current_setting('app.role_policy_bypass', TRUE);
    RETURN lower(COALESCE(v_raw, 'off')) IN ('1', 'true', 'on', 'yes');
END
$$;

CREATE OR REPLACE FUNCTION fn_enforce_sec_role_policy_lock()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_role_id BIGINT;
    v_role_code TEXT;
    v_locked BOOLEAN := FALSE;
BEGIN
    IF fn_is_role_policy_bypass_enabled() THEN
        RETURN COALESCE(NEW, OLD);
    END IF;

    v_role_id := COALESCE(NEW.role_id, OLD.role_id);
    IF v_role_id IS NULL THEN
        RETURN COALESCE(NEW, OLD);
    END IF;

    SELECT r.code
    INTO v_role_code
    FROM sec_roles r
    WHERE r.id = v_role_id;

    IF v_role_code IS NULL THEN
        RETURN COALESCE(NEW, OLD);
    END IF;

    SELECT l.is_locked
    INTO v_locked
    FROM sec_role_policy_lock l
    WHERE upper(l.role_code) = upper(v_role_code);

    IF COALESCE(v_locked, FALSE) THEN
        RAISE EXCEPTION 'Role policy is locked for role % (sec_role_action_access). Set app.role_policy_bypass=on for controlled migration.', v_role_code;
    END IF;

    RETURN COALESCE(NEW, OLD);
END
$$;

DROP TRIGGER IF EXISTS trg_enforce_sec_role_policy_lock ON sec_role_action_access;
CREATE TRIGGER trg_enforce_sec_role_policy_lock
BEFORE INSERT OR UPDATE OR DELETE ON sec_role_action_access
FOR EACH ROW
EXECUTE FUNCTION fn_enforce_sec_role_policy_lock();

CREATE OR REPLACE FUNCTION fn_enforce_sec_roles_policy_lock()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_role_code TEXT;
    v_locked BOOLEAN := FALSE;
BEGIN
    IF fn_is_role_policy_bypass_enabled() THEN
        RETURN COALESCE(NEW, OLD);
    END IF;

    v_role_code := COALESCE(NEW.code, OLD.code);
    IF v_role_code IS NULL THEN
        RETURN COALESCE(NEW, OLD);
    END IF;

    SELECT l.is_locked
    INTO v_locked
    FROM sec_role_policy_lock l
    WHERE upper(l.role_code) = upper(v_role_code);

    IF COALESCE(v_locked, FALSE) THEN
        RAISE EXCEPTION 'Role policy is locked for role % (sec_roles). Set app.role_policy_bypass=on for controlled migration.', v_role_code;
    END IF;

    RETURN COALESCE(NEW, OLD);
END
$$;

DROP TRIGGER IF EXISTS trg_enforce_sec_roles_policy_lock ON sec_roles;
CREATE TRIGGER trg_enforce_sec_roles_policy_lock
BEFORE UPDATE OR DELETE ON sec_roles
FOR EACH ROW
EXECUTE FUNCTION fn_enforce_sec_roles_policy_lock();
