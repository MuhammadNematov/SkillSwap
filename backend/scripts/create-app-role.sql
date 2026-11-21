-- SkillSwap: права роли приложения на боевой базе (этап 7, шаг 9).
--
-- Зачем. Журнал аудита (таблица "AuditLogs") должен быть только для добавления: даже при взломе приложения злоумышленник не должен иметь возможности подчистить следы.
-- Поэтому приложение подключается не под владельцем схемы, а под отдельной ролью skillswap_app, у которой на "AuditLogs" есть только SELECT и INSERT
-- (нет UPDATE, DELETE и TRUNCATE). На остальные таблицы роль получает обычные SELECT, INSERT, UPDATE, DELETE.
--
-- Кто запускает. Владелец схемы (та роль, под которой применяются миграции), после применения миграций:
--   psql -v ON_ERROR_STOP=1 -d skillswap -f create-app-role.sql
-- Роль нужно создать заранее отдельной командой, чтобы пароль не лежал в репозитории (пароль взять из хранилища секретов):
--   CREATE ROLE skillswap_app LOGIN PASSWORD '<пароль>';
-- Скрипт можно запускать повторно: после каждого деплоя с новыми миграциями его нужно выполнять заново (он выдаёт права на все таблицы, существующие на этот момент).
-- Строка подключения приложения: ConnectionStrings__Default с Username=skillswap_app. Миграции применяются строкой владельца, не приложения.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'skillswap_app') THEN
        RAISE EXCEPTION 'Роль skillswap_app не создана. Сначала: CREATE ROLE skillswap_app LOGIN PASSWORD ''<пароль>'';';
    END IF;
END
$$;

-- Подключаться к базе роль может; суперпользователем, владельцем и создателем объектов в схеме public она не является.
DO $$
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO skillswap_app', current_database());
END
$$;

GRANT USAGE ON SCHEMA public TO skillswap_app;

-- Обычные права на данные: приложение читает и пишет все таблицы.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO skillswap_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO skillswap_app;

-- Журнал аудита: только добавление и чтение. Явный REVOKE нужен, чтобы повторный запуск и права, выданные когда-то раньше, не оставили лишнего.
REVOKE UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON TABLE public."AuditLogs" FROM skillswap_app;
GRANT SELECT, INSERT ON TABLE public."AuditLogs" TO skillswap_app;

-- История миграций: приложение в боевой среде схему не меняет и только читает её (если таблица есть).
DO $$
BEGIN
    IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE public."__EFMigrationsHistory" FROM skillswap_app;
        GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO skillswap_app;
    END IF;
END
$$;

-- Новые таблицы, которые создадут будущие миграции под владельцем: те же обычные права. Для "AuditLogs" это не действует (она уже существует), а любой другой таблице
-- с требованием «только добавление» права нужно сузить в этом файле при её появлении.
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO skillswap_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO skillswap_app;

-- Hangfire (очередь фоновых задач) живёт в своей схеме и сам создаёт и обновляет свои таблицы при старте (PrepareSchemaIfNecessary), поэтому роли нужны там и права на создание.
-- Журнала аудита в этой схеме нет, сужать нечего.
CREATE SCHEMA IF NOT EXISTS hangfire;
GRANT USAGE, CREATE ON SCHEMA hangfire TO skillswap_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA hangfire TO skillswap_app;
GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA hangfire TO skillswap_app;
