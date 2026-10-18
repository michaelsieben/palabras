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
        alternative.ToTable("word_alternatives");
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

        var topic = modelBuilder.Entity<WordTopic>();
        topic.ToTable("word_topics");
        topic.HasKey(value => new { value.WordId, value.Topic });
        topic.Property(value => value.WordId).HasColumnName("word_id");
        topic.Property(value => value.Topic).HasColumnName("topic").IsRequired();
        topic.HasOne<Word>().WithMany().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);

        var revision = modelBuilder.Entity<WordRevision>();
        revision.ToTable("word_revisions", table =>
        {
            table.HasCheckConstraint("ck_word_revisions_action", "action IN ('import', 'approve', 'reject', 'needs_change', 'edit', 'retire')");
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
        ConfigureSrs(modelBuilder);
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
