using Microsoft.EntityFrameworkCore;

namespace Palabras.Persistence;

internal sealed class PalabrasDbContext : DbContext
{
    public PalabrasDbContext(DbContextOptions<PalabrasDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Word> Words => Set<Word>();
    public DbSet<Sense> Senses => Set<Sense>();
    public DbSet<WordAlternative> WordAlternatives => Set<WordAlternative>();
    public DbSet<WordTopic> WordTopics => Set<WordTopic>();
    public DbSet<WordRevision> WordRevisions => Set<WordRevision>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<ReviewLog> ReviewLogs => Set<ReviewLog>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<DayStatus> DayStatuses => Set<DayStatus>();
    public DbSet<DaySkip> DaySkips => Set<DaySkip>();
    public DbSet<PracticeSession> PracticeSessions => Set<PracticeSession>();
    public DbSet<AppMeta> AppMetas => Set<AppMeta>();
    public DbSet<WordReview> WordReviews => Set<WordReview>();
    public DbSet<CouncilRun> CouncilRuns => Set<CouncilRun>();
    public DbSet<CouncilModelResult> CouncilModelResults => Set<CouncilModelResult>();
    public DbSet<WordProvenance> WordProvenances => Set<WordProvenance>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<ReminderSettings> ReminderSettings => Set<ReminderSettings>();
    public DbSet<ReminderLog> ReminderLogs => Set<ReminderLog>();
    public DbSet<PushDelivery> PushDeliveries => Set<PushDelivery>();
    public DbSet<NotificationAction> NotificationActions => Set<NotificationAction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<User>();
        user.ToTable("users", table =>
        {
            table.HasCheckConstraint("ck_users_role", "role IN ('owner', 'reviewer')");
            table.HasCheckConstraint("ck_users_username", "length(username) BETWEEN 3 AND 32 AND username = lower(trim(username)) COLLATE BINARY AND username NOT GLOB '*[^a-z0-9._-]*'");
        });
        user.HasKey(value => value.Id);
        user.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        user.Property(value => value.Username).HasColumnName("username").UseCollation("NOCASE").IsRequired();
        user.HasIndex(value => value.Username).IsUnique();
        user.Property(value => value.Role).HasColumnName("role").IsRequired();
        // A unique role plus its CHECK permits exactly one owner and one reviewer, at most.
        user.HasIndex(value => value.Role).IsUnique();
        user.Property(value => value.PasswordHash).HasColumnName("password_hash").IsRequired();
        user.Property(value => value.CreatedAt).HasColumnName("created_at");
        user.Property(value => value.UpdatedAt).HasColumnName("updated_at");
        user.Property(value => value.LastLoginAt).HasColumnName("last_login_at");
        user.Property(value => value.Timezone).HasColumnName("timezone");

        var word = modelBuilder.Entity<Word>();
        word.ToTable("words", table =>
        {
            table.HasCheckConstraint("ck_words_article", "article IN ('el', 'la', 'los', 'las')");
            table.HasCheckConstraint("ck_words_gender", "gender IN ('masculine', 'feminine', 'common', 'not_applicable')");
            table.HasCheckConstraint("ck_words_status", "status IN ('draft', 'pending_review', 'approved', 'needs_change', 'rejected', 'retired')");
            // M4: NULL for every hand-authored word; only a council import sets a band (ΔF:563).
            table.HasCheckConstraint("ck_words_council_band", "council_band IN ('alta', 'media')");
            // M3b: 'manual' is the corpus and the default, so an old image's INSERT still succeeds.
            table.HasCheckConstraint("ck_words_source", "source IN ('council', 'manual', 'reviewer')");
        });
        word.HasKey(value => value.Id);
        word.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        word.Property(value => value.Key).HasColumnName("key").IsRequired();
        word.HasIndex(value => value.Key).IsUnique();
        word.Property(value => value.Headword).HasColumnName("headword").IsRequired();
        word.Property(value => value.Article).HasColumnName("article");
        word.Property(value => value.Gender).HasColumnName("gender");
        word.Property(value => value.Pos).HasColumnName("pos").IsRequired();
        word.Property(value => value.Cefr).HasColumnName("cefr").IsRequired();
        // M5 ramp: nullable, no CHECK and no FK. Anything that forces a SQLite rebuild here would
        // widen the ReviewGate crash window (docs/08-upgrade-rollback.md §G).
        word.Property(value => value.Difficulty).HasColumnName("difficulty");
        word.Property(value => value.RegionScope).HasColumnName("region_scope").IsRequired();
        word.Property(value => value.Register).HasColumnName("register").IsRequired();
        word.Property(value => value.RegisterNotes).HasColumnName("register_notes");
        word.Property(value => value.RegionNotes).HasColumnName("region_notes");
        word.Property(value => value.Notes).HasColumnName("notes");
        word.Property(value => value.Plural).HasColumnName("plural");
        word.Property(value => value.Status).HasColumnName("status").IsRequired();
        word.HasIndex(value => value.Status);
        word.Property(value => value.Version).HasColumnName("version").HasDefaultValue(1);
        word.Property(value => value.ContentHash).HasColumnName("content_hash").IsRequired();
        word.Property(value => value.ProvenanceMethod).HasColumnName("provenance_method").IsRequired();
        word.Property(value => value.ProvenanceRun).HasColumnName("provenance_run");
        word.Property(value => value.CouncilAgreement).HasColumnName("council_agreement");
        word.Property(value => value.CouncilBand).HasColumnName("council_band");
        word.Property(value => value.ReducedConfidence).HasColumnName("reduced_confidence").HasDefaultValue(0);
        word.Property(value => value.LintWarnings).HasColumnName("lint_warnings");
        word.Property(value => value.Source).HasColumnName("source").IsRequired().HasDefaultValue("manual");
        word.Property(value => value.CouncilRunId).HasColumnName("council_run_id");
        word.HasOne<CouncilRun>().WithMany().HasForeignKey(value => value.CouncilRunId)
            .OnDelete(DeleteBehavior.NoAction);
        word.HasIndex(value => value.CouncilRunId).HasDatabaseName("ix_words_council_run_id");
        word.Property(value => value.StatusChangedAt).HasColumnName("status_changed_at");
        word.Property(value => value.ApprovedVersion).HasColumnName("approved_version");
        word.Property(value => value.CreatedAt).HasColumnName("created_at");
        word.Property(value => value.UpdatedAt).HasColumnName("updated_at");

        var sense = modelBuilder.Entity<Sense>();
        sense.ToTable("senses");
        sense.HasKey(value => value.Id);
        sense.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        sense.Property(value => value.WordId).HasColumnName("word_id");
        sense.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        sense.Property(value => value.SenseOrder).HasColumnName("sense_order");
        sense.HasIndex(value => new { value.WordId, value.SenseOrder }).IsUnique();
        sense.Property(value => value.EnGloss).HasColumnName("en_gloss").IsRequired();
        sense.Property(value => value.EnAccepted).HasColumnName("en_accepted").IsRequired().HasDefaultValue("[]");
        sense.Property(value => value.EsAccepted).HasColumnName("es_accepted").IsRequired().HasDefaultValue("[]");
        sense.Property(value => value.ExampleEs).HasColumnName("example_es").IsRequired();
        sense.Property(value => value.ExampleEn).HasColumnName("example_en").IsRequired();
        sense.Property(value => value.AccentsAreThePoint).HasColumnName("accents_are_the_point").HasDefaultValue(false);
        sense.Property(value => value.Notes).HasColumnName("notes");

        var alternative = modelBuilder.Entity<WordAlternative>();
        alternative.ToTable("word_alternatives", table =>
        {
            // M3b alternatives v2 (ΔE.3). NULL passes a SQLite CHECK, so legacy rows stay valid.
            table.HasCheckConstraint("ck_word_alternatives_field",
                "field IN ('lemma', 'gloss', 'example', 'regional_note', 'regional_variant', 'register_variant')");
            table.HasCheckConstraint("ck_word_alternatives_region", "region IN ('ES', 'ES-NA')");
            table.HasCheckConstraint("ck_word_alternatives_register", "register IN ('neutral', 'colloquial', 'formal')");
            table.HasCheckConstraint("ck_word_alternatives_source", "source IN ('council', 'reviewer', 'manual', 'previous')");
        });
        alternative.HasKey(value => value.Id);
        alternative.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        alternative.Property(value => value.WordId).HasColumnName("word_id");
        alternative.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        alternative.Property(value => value.Kind).HasColumnName("kind").IsRequired();
        alternative.Property(value => value.Value).HasColumnName("value").IsRequired();
        alternative.Property(value => value.Gloss).HasColumnName("gloss");
        alternative.Property(value => value.Reason).HasColumnName("reason").IsRequired();
        alternative.Property(value => value.TargetKey).HasColumnName("target_key");
        alternative.Property(value => value.Example).HasColumnName("example");
        alternative.Property(value => value.Rank).HasColumnName("rank");
        alternative.Property(value => value.Field).HasColumnName("field");
        alternative.Property(value => value.Support).HasColumnName("support").HasDefaultValue(0);
        alternative.Property(value => value.Models).HasColumnName("models");
        alternative.Property(value => value.Region).HasColumnName("region");
        alternative.Property(value => value.Register).HasColumnName("register");
        alternative.Property(value => value.WordVersion).HasColumnName("word_version").HasDefaultValue(0);
        alternative.Property(value => value.Source).HasColumnName("source").IsRequired().HasDefaultValue("manual");

        var topic = modelBuilder.Entity<WordTopic>();
        topic.ToTable("word_topics");
        topic.HasKey(value => new { value.WordId, value.Topic });
        topic.Property(value => value.WordId).HasColumnName("word_id");
        topic.Property(value => value.Topic).HasColumnName("topic").IsRequired();
        topic.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);

        var revision = modelBuilder.Entity<WordRevision>();
        revision.ToTable("word_revisions", table =>
        {
            table.HasCheckConstraint("ck_word_revisions_action", "action IN ('import', 'approve', 'reject', 'needs_change', 'edit', 'retire', 'demote')");
        });
        revision.HasKey(value => value.Id);
        revision.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        revision.Property(value => value.WordId).HasColumnName("word_id");
        revision.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        revision.Property(value => value.Version).HasColumnName("version");
        revision.Property(value => value.ActorUserId).HasColumnName("actor_user_id");
        revision.HasOne<User>().WithMany().HasForeignKey(value => value.ActorUserId);
        revision.Property(value => value.Action).HasColumnName("action").IsRequired();
        revision.Property(value => value.SnapshotJson).HasColumnName("snapshot_json").IsRequired();
        revision.Property(value => value.CreatedAt).HasColumnName("created_at");
        ConfigureReview(modelBuilder);
        ConfigureCouncil(modelBuilder);
        ConfigureSrs(modelBuilder);
        ConfigureDayTracking(modelBuilder);
        ConfigureAppMeta(modelBuilder);
        ConfigurePush(modelBuilder);
    }

    // M4: word_reviews, the append-only reviewer verdict log (ΔF:609-618). council_run_id is indexed
    // but deliberately has no FK: runs are never deleted, the write path only supplies an id it just
    // inserted, and adding the constraint would rebuild this table (Q-M3-3).
    private static void ConfigureReview(ModelBuilder modelBuilder)
    {
        var review = modelBuilder.Entity<WordReview>();
        review.ToTable("word_reviews", table =>
        {
            table.HasCheckConstraint("ck_word_reviews_action",
                "action IN ('approve', 'approve_alt', 'approve_edit', 'reject', 'needs_change', 'demote')");
            // NULL passes a SQLite CHECK, so a verdict that names no field is still valid.
            table.HasCheckConstraint("ck_word_reviews_field",
                "field IN ('lemma', 'gloss', 'example', 'regional_note', 'regional_variant', 'register_variant', 'article', 'gender', 'accepted', 'whole')");
        });
        review.HasKey(value => value.Id);
        review.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        review.Property(value => value.WordId).HasColumnName("word_id");
        review.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        review.Property(value => value.WordVersion).HasColumnName("word_version");
        review.Property(value => value.SenseOrder).HasColumnName("sense_order");
        review.Property(value => value.ReviewerId).HasColumnName("reviewer_id");
        review.HasOne<User>().WithMany().HasForeignKey(value => value.ReviewerId).OnDelete(DeleteBehavior.NoAction);
        review.Property(value => value.Action).HasColumnName("action").IsRequired();
        review.Property(value => value.Field).HasColumnName("field");
        review.Property(value => value.ChosenAlternativeId).HasColumnName("chosen_alternative_id");
        // SET NULL, not NO ACTION: deleting a word cascades through word_alternatives, and a plain
        // constraint would make that cascade abort. The verdict itself must survive either way.
        review.HasOne<WordAlternative>().WithMany().HasForeignKey(value => value.ChosenAlternativeId)
            .OnDelete(DeleteBehavior.SetNull);
        review.Property(value => value.EditDiff).HasColumnName("edit_diff");
        review.Property(value => value.Note).HasColumnName("note");
        review.Property(value => value.ClientReviewId).HasColumnName("client_review_id").IsRequired();
        // Offline replay key: a retried action is a duplicate row, never a second verdict.
        review.HasIndex(value => value.ClientReviewId).IsUnique();
        review.Property(value => value.ViaBatch).HasColumnName("via_batch").HasDefaultValue(0);
        review.Property(value => value.CouncilRunId).HasColumnName("council_run_id");
        review.Property(value => value.AgreementAtReview).HasColumnName("agreement_at_review");
        review.Property(value => value.DecisionMs).HasColumnName("decision_ms");
        review.Property(value => value.CreatedAt).HasColumnName("created_at");
        review.HasIndex(value => new { value.WordId, value.WordVersion });
        review.HasIndex(value => value.CouncilRunId).HasDatabaseName("ix_word_reviews_council_run_id");
    }

    // M3b: the council import's three tables (design-amendment.md ΔF:556-583). Every row here is
    // written by the council importer only; nothing in the learner or reviewer path reads them yet.
    private static void ConfigureCouncil(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<CouncilRun>();
        run.ToTable("council_runs", table =>
        {
            table.HasCheckConstraint("ck_council_runs_mode", "mode IN ('propose', 'rework')");
        });
        run.HasKey(value => value.Id);
        run.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        run.Property(value => value.RunId).HasColumnName("run_id").IsRequired();
        run.HasIndex(value => value.RunId).IsUnique();
        run.Property(value => value.Mode).HasColumnName("mode").IsRequired();
        run.Property(value => value.PromptVersions).HasColumnName("prompt_versions").IsRequired();
        run.Property(value => value.StyleGuideSha256).HasColumnName("style_guide_sha256").IsRequired();
        run.Property(value => value.Topic).HasColumnName("topic");
        run.Property(value => value.Cefr).HasColumnName("cefr");
        run.Property(value => value.RequestedCount).HasColumnName("requested_count");
        run.Property(value => value.ModelsRequested).HasColumnName("models_requested");
        run.Property(value => value.ModelsSucceededPropose).HasColumnName("models_succeeded_propose");
        run.Property(value => value.ModelsSucceededCritique).HasColumnName("models_succeeded_critique");
        run.Property(value => value.ReducedConfidence).HasColumnName("reduced_confidence").HasDefaultValue(0);
        run.Property(value => value.CandidatesCount).HasColumnName("candidates_count");
        run.Property(value => value.DroppedCount).HasColumnName("dropped_count").HasDefaultValue(0);
        run.Property(value => value.CandidatesSha256).HasColumnName("candidates_sha256").IsRequired();
        run.Property(value => value.CreatedAt).HasColumnName("created_at");
        run.Property(value => value.ImportedAt).HasColumnName("imported_at");
        // The L2 report and the feedback export read runs by recency, never by surrogate id.
        run.HasIndex(value => value.CreatedAt).HasDatabaseName("ix_council_runs_created_at");

        var result = modelBuilder.Entity<CouncilModelResult>();
        result.ToTable("council_model_results", table =>
        {
            table.HasCheckConstraint("ck_council_model_results_phase", "phase IN ('propose', 'critique', 'topup')");
            table.HasCheckConstraint("ck_council_model_results_status", "status IN ('ok', 'failed', 'timeout', 'invalid')");
        });
        result.HasKey(value => value.Id);
        result.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        result.Property(value => value.CouncilRunId).HasColumnName("council_run_id");
        result.HasOne<CouncilRun>().WithMany().HasForeignKey(value => value.CouncilRunId)
            .OnDelete(DeleteBehavior.Cascade);
        result.Property(value => value.ModelId).HasColumnName("model_id").IsRequired();
        result.Property(value => value.Phase).HasColumnName("phase").IsRequired();
        result.Property(value => value.Status).HasColumnName("status").IsRequired();
        result.Property(value => value.LatencyMs).HasColumnName("latency_ms");
        result.Property(value => value.InputTokens).HasColumnName("input_tokens");
        result.Property(value => value.OutputTokens).HasColumnName("output_tokens");
        result.Property(value => value.CostUsdEst).HasColumnName("cost_usd_est");
        result.Property(value => value.InvalidCount).HasColumnName("invalid_count").HasDefaultValue(0);
        result.Property(value => value.Error).HasColumnName("error");
        // One outcome per model per phase: a retried bundle cannot double-count a model.
        result.HasIndex(value => new { value.CouncilRunId, value.ModelId, value.Phase }).IsUnique();

        var provenance = modelBuilder.Entity<WordProvenance>();
        provenance.ToTable("word_provenance", table =>
        {
            table.HasCheckConstraint("ck_word_provenance_role", "role IN ('proposed', 'critique_ok', 'critique_issue')");
        });
        provenance.HasKey(value => new { value.WordId, value.WordVersion, value.CouncilRunId, value.ModelId, value.Role });
        provenance.Property(value => value.WordId).HasColumnName("word_id");
        provenance.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        provenance.Property(value => value.WordVersion).HasColumnName("word_version");
        provenance.Property(value => value.CouncilRunId).HasColumnName("council_run_id");
        provenance.HasOne<CouncilRun>().WithMany().HasForeignKey(value => value.CouncilRunId)
            .OnDelete(DeleteBehavior.NoAction);
        provenance.Property(value => value.ModelId).HasColumnName("model_id").IsRequired();
        provenance.Property(value => value.Role).HasColumnName("role").IsRequired();
        provenance.Property(value => value.Issues).HasColumnName("issues");
        // The PK leads with word_id, so a per-run read would otherwise scan.
        provenance.HasIndex(value => value.CouncilRunId).HasDatabaseName("ix_word_provenance_run");
    }

    // M7: day_status, day_skips and practice_sessions (design.md:103-120, design-amendment.md
    // ΔF:631-640). Local dates are stored as yyyy-MM-dd TEXT in the learner's zone; the day
    // boundary is decided by LearnerClock, never by the container clock.
    private static void ConfigureDayTracking(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<PracticeSession>();
        session.ToTable("practice_sessions", table =>
        {
            table.HasCheckConstraint("ck_practice_sessions_status",
                "status IN ('in_progress', 'completed', 'abandoned')");
        });
        session.HasKey(value => value.Id);
        session.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        session.Property(value => value.UserId).HasColumnName("user_id");
        session.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        session.Property(value => value.LocalDate).HasColumnName("local_date").IsRequired();
        session.Property(value => value.StartedAtMs).HasColumnName("started_at_ms");
        session.Property(value => value.CompletedAtMs).HasColumnName("completed_at_ms");
        session.Property(value => value.DurationS).HasColumnName("duration_s");
        session.Property(value => value.PlannedS).HasColumnName("planned_s");
        session.Property(value => value.NewCount).HasColumnName("new_count");
        session.Property(value => value.ReviewCount).HasColumnName("review_count");
        session.Property(value => value.Status).HasColumnName("status").IsRequired();
        session.HasIndex(value => new { value.UserId, value.LocalDate });

        var day = modelBuilder.Entity<DayStatus>();
        day.ToTable("day_status", table =>
        {
            // 'frozen' is removed (ΔD:291, ΔF:632): only an explicit skip keeps a day alive.
            table.HasCheckConstraint("ck_day_status_status", "status IN ('done', 'skipped', 'missed')");
        });
        day.HasKey(value => new { value.UserId, value.LocalDate });
        day.Property(value => value.UserId).HasColumnName("user_id");
        day.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        day.Property(value => value.LocalDate).HasColumnName("local_date").IsRequired();
        day.Property(value => value.Status).HasColumnName("status").IsRequired();
        day.Property(value => value.SessionId).HasColumnName("session_id");
        day.HasOne<PracticeSession>().WithMany().HasForeignKey(value => value.SessionId)
            .OnDelete(DeleteBehavior.NoAction);
        day.Property(value => value.DecidedAtMs).HasColumnName("decided_at_ms");

        var skip = modelBuilder.Entity<DaySkip>();
        skip.ToTable("day_skips", table =>
        {
            table.HasCheckConstraint("ck_day_skips_source", "source IN ('app', 'notification')");
        });
        skip.HasKey(value => value.Id);
        skip.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        skip.Property(value => value.UserId).HasColumnName("user_id");
        skip.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        skip.Property(value => value.LocalDate).HasColumnName("local_date").IsRequired();
        skip.Property(value => value.Source).HasColumnName("source").IsRequired();
        skip.Property(value => value.CreatedAtMs).HasColumnName("created_at_ms");
        skip.Property(value => value.RefundedAtMs).HasColumnName("refunded_at_ms");
        // ΔF:639 / design.md:120: one row per learner per day. The rolling-window index at
        // ΔF:640 (`ix_day_skips_window ... WHERE refunded_at IS NULL`) is raw SQL in the
        // migration: EF Core merges two indexes declared over the same columns into one.
        skip.HasIndex(value => new { value.UserId, value.LocalDate }).IsUnique();
    }

    // App-wide durable markers (T-20261018-2105 step 11). The M7 rollover cutover is the first
    // key; its value is a UTC unix-ms decimal string, the repo-wide instant convention.
    private static void ConfigureAppMeta(ModelBuilder modelBuilder)
    {
        var meta = modelBuilder.Entity<AppMeta>();
        meta.ToTable("app_meta");
        meta.HasKey(value => value.Key);
        meta.Property(value => value.Key).HasColumnName("key").IsRequired();
        meta.Property(value => value.Value).HasColumnName("value").IsRequired();
    }

    // M8: the five Web Push tables (design-amendment.md ΔC.2/ΔF, plan §2.3). Every one is new, so
    // the migration creates tables and indexes only — ReviewGate stays the repo's single rebuild
    // (docs/08-upgrade-rollback.md §G) and no CHECK here can force a rebuild of an existing table.
    private static void ConfigurePush(ModelBuilder modelBuilder)
    {
        var subscription = modelBuilder.Entity<PushSubscription>();
        subscription.ToTable("push_subscriptions", table =>
        {
            table.HasCheckConstraint("ck_push_subscriptions_state", "state IN ('active', 'stale_key', 'rejected')");
        });
        subscription.HasKey(value => value.Id);
        subscription.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        subscription.Property(value => value.UserId).HasColumnName("user_id");
        subscription.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        // The endpoint is the natural key of a push subscription: the same device re-PUTs the same
        // string, and the upsert must land on the existing row without a second registration.
        subscription.Property(value => value.Endpoint).HasColumnName("endpoint").IsRequired();
        subscription.HasIndex(value => value.Endpoint).IsUnique();
        subscription.Property(value => value.P256dh).HasColumnName("p256dh").IsRequired();
        subscription.Property(value => value.Auth).HasColumnName("auth").IsRequired();
        subscription.Property(value => value.VapidKeyId).HasColumnName("vapid_key_id").IsRequired();
        subscription.Property(value => value.DeviceLabel).HasColumnName("device_label");
        subscription.Property(value => value.State).HasColumnName("state").IsRequired().HasDefaultValue("active");
        subscription.Property(value => value.CreatedAtMs).HasColumnName("created_at_ms");
        subscription.Property(value => value.UpdatedAtMs).HasColumnName("updated_at_ms");
        subscription.Property(value => value.LastSuccessAtMs).HasColumnName("last_success_at_ms");
        subscription.Property(value => value.LastFailureAtMs).HasColumnName("last_failure_at_ms");
        subscription.Property(value => value.FailureCount).HasColumnName("failure_count").HasDefaultValue(0);
        subscription.Property(value => value.LastStatus).HasColumnName("last_status");
        // The send path asks "this learner's deliverable devices" once a minute.
        subscription.HasIndex(value => new { value.UserId, value.State }).HasDatabaseName("ix_push_subscriptions_user");

        var reminder = modelBuilder.Entity<ReminderSettings>();
        reminder.ToTable("reminder_settings", table =>
        {
            // HH:mm with 15-minute steps is enforced by the API; the column only guards the length.
            table.HasCheckConstraint("ck_reminder_settings_time", "length(reminder_time) = 5");
            table.HasCheckConstraint("ck_reminder_settings_window", "send_window_min BETWEEN 15 AND 240");
        });
        reminder.HasKey(value => value.UserId);
        reminder.Property(value => value.UserId).HasColumnName("user_id").ValueGeneratedNever();
        reminder.HasOne<User>().WithOne().HasForeignKey<ReminderSettings>(value => value.UserId)
            .OnDelete(DeleteBehavior.NoAction);
        reminder.Property(value => value.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        reminder.Property(value => value.ReminderTime).HasColumnName("reminder_time").IsRequired().HasDefaultValue("22:00");
        reminder.Property(value => value.MutedUntil).HasColumnName("muted_until");
        reminder.Property(value => value.SendWindowMin).HasColumnName("send_window_min").HasDefaultValue(90);
        reminder.Property(value => value.UpdatedAtMs).HasColumnName("updated_at_ms");

        var log = modelBuilder.Entity<ReminderLog>();
        log.ToTable("reminder_log", table =>
        {
            table.HasCheckConstraint("ck_reminder_log_kind", "kind IN ('daily')");
            table.HasCheckConstraint("ck_reminder_log_channel", "channel IN ('webpush')");
            table.HasCheckConstraint("ck_reminder_log_result",
                "result IN ('sending', 'sent', 'failed', 'gone', 'window_missed', 'no_target')");
        });
        log.HasKey(value => value.Id);
        log.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        log.Property(value => value.UserId).HasColumnName("user_id");
        log.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        log.Property(value => value.LocalDate).HasColumnName("local_date").IsRequired();
        log.Property(value => value.Kind).HasColumnName("kind").IsRequired();
        log.Property(value => value.Channel).HasColumnName("channel").IsRequired();
        log.Property(value => value.Result).HasColumnName("result").IsRequired();
        log.Property(value => value.TtlS).HasColumnName("ttl_s");
        log.Property(value => value.ExpiresAtMs).HasColumnName("expires_at_ms");
        log.Property(value => value.SkipAvailable).HasColumnName("skip_available");
        log.Property(value => value.CreatedAtMs).HasColumnName("created_at_ms");
        log.Property(value => value.UpdatedAtMs).HasColumnName("updated_at_ms");
        // The claim: one decision per learner per local day per kind and channel, enforced by the
        // database so two ticks (or a restart) cannot both send.
        log.HasIndex(value => new { value.UserId, value.LocalDate, value.Kind, value.Channel }).IsUnique();

        var delivery = modelBuilder.Entity<PushDelivery>();
        delivery.ToTable("push_deliveries", table =>
        {
            table.HasCheckConstraint("ck_push_deliveries_result",
                "result IN ('pending', 'sent', 'gone', 'rejected', 'transient')");
        });
        delivery.HasKey(value => value.Id);
        delivery.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        delivery.Property(value => value.ReminderLogId).HasColumnName("reminder_log_id");
        delivery.HasOne<ReminderLog>().WithMany().HasForeignKey(value => value.ReminderLogId)
            .OnDelete(DeleteBehavior.Cascade);
        delivery.Property(value => value.SubscriptionId).HasColumnName("subscription_id");
        // SET NULL, not NO ACTION: the sender deletes a subscription on 404/410, and that delete
        // must not fail because the attempt history points at it.
        delivery.HasOne<PushSubscription>().WithMany().HasForeignKey(value => value.SubscriptionId)
            .OnDelete(DeleteBehavior.SetNull);
        delivery.Property(value => value.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        delivery.Property(value => value.Result).HasColumnName("result").IsRequired();
        delivery.Property(value => value.LastStatus).HasColumnName("last_status");
        delivery.Property(value => value.UpdatedAtMs).HasColumnName("updated_at_ms");
        // One attempt row per device per decision; a retry updates it and bumps `attempts`.
        delivery.HasIndex(value => new { value.ReminderLogId, value.SubscriptionId }).IsUnique();

        var action = modelBuilder.Entity<NotificationAction>();
        action.ToTable("notification_actions", table =>
        {
            table.HasCheckConstraint("ck_notification_actions_action", "action IN ('open', 'skip', 'mute')");
            table.HasCheckConstraint("ck_notification_actions_result",
                "result IN ('ok', 'skip_unavailable', 'already_done', 'unauthenticated', 'error')");
            // NULL passes a SQLite CHECK, so an action without a mute choice stays valid.
            table.HasCheckConstraint("ck_notification_actions_mute_choice",
                "mute_choice IN ('tomorrow', '3d', '7d', 'disable', 'time_changed', 'cancel')");
        });
        action.HasKey(value => value.Id);
        action.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        action.Property(value => value.UserId).HasColumnName("user_id");
        action.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        action.Property(value => value.ReminderLogId).HasColumnName("reminder_log_id");
        // SET NULL: the audit row outlives a purged decision, and it is the only record of the tap.
        action.HasOne<ReminderLog>().WithMany().HasForeignKey(value => value.ReminderLogId)
            .OnDelete(DeleteBehavior.SetNull);
        action.Property(value => value.LocalDate).HasColumnName("local_date").IsRequired();
        action.Property(value => value.Action).HasColumnName("action").IsRequired();
        action.Property(value => value.Result).HasColumnName("result").IsRequired();
        action.Property(value => value.MuteChoice).HasColumnName("mute_choice");
        action.Property(value => value.CreatedAtMs).HasColumnName("created_at_ms");
    }

    private static void ConfigureSrs(ModelBuilder modelBuilder)
    {
        var settings = modelBuilder.Entity<UserSettings>();
        settings.ToTable("user_settings");
        settings.HasKey(value => value.UserId);
        settings.Property(value => value.UserId).HasColumnName("user_id").ValueGeneratedNever();
        settings.HasOne<User>().WithOne().HasForeignKey<UserSettings>(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        settings.Property(value => value.DesiredRetention).HasColumnName("desired_retention").HasDefaultValue(0.90);
        settings.Property(value => value.NewPerDay).HasColumnName("new_per_day").HasDefaultValue(12).HasSentinel(-1);
        settings.Property(value => value.MaxReviewsPerDay).HasColumnName("max_reviews_per_day").HasDefaultValue(200).HasSentinel(-1);
        settings.Property(value => value.BacklogThreshold).HasColumnName("backlog_threshold").HasDefaultValue(150).HasSentinel(-1);
        settings.Property(value => value.SessionTargetS).HasColumnName("session_target_s").HasDefaultValue(600);
        settings.Property(value => value.SessionMaxS).HasColumnName("session_max_s").HasDefaultValue(900);
        settings.Property(value => value.MaxIntervalDays).HasColumnName("max_interval_days").HasDefaultValue(730);
        settings.Property(value => value.EnableFuzz).HasColumnName("enable_fuzz").HasDefaultValue(true).HasSentinel(true);
        settings.Property(value => value.LearningSteps).HasColumnName("learning_steps").HasDefaultValue("[\"00:01:00\",\"00:10:00\"]");
        settings.Property(value => value.RelearningSteps).HasColumnName("relearning_steps").HasDefaultValue("[\"00:10:00\"]");
        settings.Property(value => value.LeechLapses).HasColumnName("leech_lapses").HasDefaultValue(8);
        settings.Property(value => value.WeightsJson).HasColumnName("weights_json");
        settings.Property(value => value.WeightsVersion).HasColumnName("weights_version").HasDefaultValue(0);

        var card = modelBuilder.Entity<Card>();
        card.ToTable("cards", table =>
        {
            table.HasCheckConstraint("ck_cards_direction", "direction IN ('es_en', 'en_es')");
            table.HasCheckConstraint("ck_cards_state", "state IN (0, 1, 2, 3)");
            table.HasCheckConstraint("ck_cards_suspended_reason", "suspended_reason IN ('word_not_approved', 'sense_removed', 'manual', 'leech')");
        });
        card.HasKey(value => value.Id);
        card.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        card.Property(value => value.UserId).HasColumnName("user_id");
        card.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        card.Property(value => value.SenseId).HasColumnName("sense_id");
        card.HasOne<Sense>().WithMany().HasForeignKey(value => value.SenseId).OnDelete(DeleteBehavior.Cascade);
        card.Property(value => value.Direction).HasColumnName("direction").IsRequired();
        card.Property(value => value.Epoch).HasColumnName("epoch").HasDefaultValue(0);
        card.Property(value => value.State).HasColumnName("state");
        card.Property(value => value.Step).HasColumnName("step");
        card.Property(value => value.Stability).HasColumnName("stability");
        card.Property(value => value.Difficulty).HasColumnName("difficulty");
        card.Property(value => value.DueMs).HasColumnName("due_ms");
        card.Property(value => value.LastReviewMs).HasColumnName("last_review_ms");
        card.Property(value => value.Reps).HasColumnName("reps").HasDefaultValue(0);
        card.Property(value => value.Lapses).HasColumnName("lapses").HasDefaultValue(0);
        card.Property(value => value.SuspendedReason).HasColumnName("suspended_reason");
        card.Property(value => value.BuriedUntilLocalDate).HasColumnName("buried_until_local_date");
        card.Property(value => value.IsLeech).HasColumnName("is_leech").HasDefaultValue(false);
        card.Property(value => value.IntroducedLocalDate).HasColumnName("introduced_local_date");
        card.Property(value => value.CreatedAt).HasColumnName("created_at");
        card.HasIndex(value => new { value.SenseId, value.UserId, value.Direction }).IsUnique();
        card.HasIndex(value => new { value.UserId, value.DueMs }).HasDatabaseName("ix_cards_due").HasFilter("suspended_reason IS NULL AND state <> 0");
        card.HasIndex(value => new { value.UserId, value.CreatedAt }).HasDatabaseName("ix_cards_new").HasFilter("suspended_reason IS NULL AND state = 0");

        var log = modelBuilder.Entity<ReviewLog>();
        log.ToTable("review_log", table =>
        {
            table.HasCheckConstraint("ck_review_log_rating", "rating BETWEEN 1 AND 4");
            table.HasCheckConstraint("ck_review_log_mode", "mode IN ('recognition', 'typed', 'cloze')");
        });
        log.HasKey(value => value.Id);
        log.Property(value => value.Id).HasColumnName("id").ValueGeneratedOnAdd();
        log.Property(value => value.ClientAttemptId).HasColumnName("client_attempt_id").IsRequired();
        log.HasIndex(value => value.ClientAttemptId).IsUnique();
        log.Property(value => value.UserId).HasColumnName("user_id");
        log.HasOne<User>().WithMany().HasForeignKey(value => value.UserId).OnDelete(DeleteBehavior.NoAction);
        log.Property(value => value.WordId).HasColumnName("word_id");
        log.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.NoAction);
        log.Property(value => value.CardId).HasColumnName("card_id");
        log.HasOne<Card>().WithMany().HasForeignKey(value => value.CardId).OnDelete(DeleteBehavior.NoAction);
        log.Property(value => value.CardEpoch).HasColumnName("card_epoch");
        log.Property(value => value.SessionId).HasColumnName("session_id");
        log.Property(value => value.Rating).HasColumnName("rating");
        log.Property(value => value.ReviewedAtMs).HasColumnName("reviewed_at_ms");
        log.Property(value => value.ReceivedAtMs).HasColumnName("received_at_ms");
        log.Property(value => value.ElapsedDays).HasColumnName("elapsed_days");
        log.Property(value => value.IntervalBeforeDays).HasColumnName("interval_before_days");
        log.Property(value => value.IntervalAfterDays).HasColumnName("interval_after_days");
        log.Property(value => value.DurationMs).HasColumnName("duration_ms");
        log.Property(value => value.Mode).HasColumnName("mode").IsRequired();
        log.Property(value => value.IsCorrectMeaning).HasColumnName("is_correct_meaning");
        log.Property(value => value.IsCorrectForm).HasColumnName("is_correct_form");
        log.Property(value => value.FeedbackKind).HasColumnName("feedback_kind");
        log.Property(value => value.Overridden).HasColumnName("overridden").HasDefaultValue(false);
        log.Property(value => value.AnswerText).HasColumnName("answer_text");
        log.Property(value => value.StateBefore).HasColumnName("state_before");
        log.Property(value => value.StabilityBefore).HasColumnName("stability_before");
        log.Property(value => value.DifficultyBefore).HasColumnName("difficulty_before");
        log.Property(value => value.StateAfter).HasColumnName("state_after");
        log.Property(value => value.StabilityAfter).HasColumnName("stability_after");
        log.Property(value => value.DifficultyAfter).HasColumnName("difficulty_after");
        log.Property(value => value.DueAfterMs).HasColumnName("due_after_ms");
        log.Property(value => value.WeightsVersion).HasColumnName("weights_version");
        log.Property(value => value.CountsForSrs).HasColumnName("counts_for_srs").HasDefaultValue(true).HasSentinel(true);
        log.HasIndex(value => new { value.CardId, value.ReviewedAtMs });
        log.HasIndex(value => new { value.UserId, value.ReviewedAtMs });
    }
}
