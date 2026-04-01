CREATE OR REPLACE FUNCTION gl_accounts_biu_set_hierarchy()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
DECLARE
    v_parent_level INT;
    v_parent_path LTREE;
    v_parent_is_posting BOOLEAN;
    v_parent_account_type VARCHAR(20);
    v_cycle_exists BOOLEAN;
    v_label TEXT;
BEGIN
    NEW.account_code := UPPER(BTRIM(NEW.account_code));
    NEW.account_name := BTRIM(NEW.account_name);
    NEW.account_type := UPPER(BTRIM(NEW.account_type));
    NEW.normal_balance := UPPER(BTRIM(NEW.normal_balance));
    NEW.updated_at := NOW();

    IF NEW.account_type NOT IN ('ASSET', 'LIABILITY', 'EQUITY', 'REVENUE', 'EXPENSE') THEN
        RAISE EXCEPTION 'Invalid account_type: %', NEW.account_type;
    END IF;

    IF NEW.normal_balance NOT IN ('D', 'C') THEN
        RAISE EXCEPTION 'Invalid normal_balance: %', NEW.normal_balance;
    END IF;

    IF NEW.account_code !~ '^[A-Z0-9]{2}\.[0-9]{5}\.[0-9]{3}$' THEN
        RAISE EXCEPTION 'Invalid account_code format %. Expected XX.99999.999.', NEW.account_code;
    END IF;

    IF (NEW.account_type IN ('ASSET', 'EXPENSE') AND NEW.normal_balance <> 'D')
       OR (NEW.account_type IN ('LIABILITY', 'EQUITY', 'REVENUE') AND NEW.normal_balance <> 'C') THEN
        RAISE EXCEPTION 'account_type % must use normal_balance %',
            NEW.account_type,
            CASE WHEN NEW.account_type IN ('ASSET', 'EXPENSE') THEN 'D' ELSE 'C' END;
    END IF;

    v_label := gl_accounts_normalize_label(NEW.account_code);
    IF v_label = '' THEN
        RAISE EXCEPTION 'account_code is not valid for hierarchy path.';
    END IF;

    IF NEW.parent_account_id IS NULL THEN
        NEW.account_level := 1;
        NEW.full_path := TEXT2LTREE(v_label);
    ELSE
        SELECT account_level, full_path, is_posting, account_type
        INTO v_parent_level, v_parent_path, v_parent_is_posting, v_parent_account_type
        FROM gl_accounts
        WHERE id = NEW.parent_account_id
          AND company_id = NEW.company_id;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'Parent account % not found in company %', NEW.parent_account_id, NEW.company_id;
        END IF;

        IF v_parent_is_posting THEN
            RAISE EXCEPTION 'Parent account % is posting and cannot have children.', NEW.parent_account_id;
        END IF;

        IF COALESCE(v_parent_account_type, '') = '' THEN
            RAISE EXCEPTION 'Parent account % has invalid account_type.', NEW.parent_account_id;
        END IF;

        IF NEW.account_type <> v_parent_account_type THEN
            RAISE EXCEPTION 'Child account_type % must match parent account_type %.',
                NEW.account_type,
                v_parent_account_type;
        END IF;

        IF NEW.id IS NOT NULL THEN
            WITH RECURSIVE parents AS (
                SELECT id, parent_account_id
                FROM gl_accounts
                WHERE id = NEW.parent_account_id
                  AND company_id = NEW.company_id
                UNION ALL
                SELECT g.id, g.parent_account_id
                FROM gl_accounts g
                JOIN parents p ON p.parent_account_id = g.id
                WHERE g.company_id = NEW.company_id
            )
            SELECT EXISTS (
                SELECT 1
                FROM parents
                WHERE id = NEW.id
            )
            INTO v_cycle_exists;

            IF v_cycle_exists THEN
                RAISE EXCEPTION 'Hierarchy cycle detected for account id %', NEW.id;
            END IF;
        END IF;

        NEW.account_level := v_parent_level + 1;
        NEW.full_path := v_parent_path || TEXT2LTREE(v_label);
    END IF;

    IF NEW.id IS NOT NULL
       AND NEW.is_posting
       AND EXISTS (
           SELECT 1
           FROM gl_accounts c
           WHERE c.parent_account_id = NEW.id
       ) THEN
        RAISE EXCEPTION 'Posting account % cannot have child accounts.', NEW.id;
    END IF;

    RETURN NEW;
END
$$;
