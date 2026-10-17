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
    }
}
