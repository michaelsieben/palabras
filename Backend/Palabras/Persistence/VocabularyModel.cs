using Microsoft.EntityFrameworkCore;

namespace Palabras.Persistence;

internal static class VocabularyModel
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        var word = modelBuilder.Entity<Word>();
        word.ToTable("words", table => table.HasCheckConstraint("ck_words_status", "status IN ('draft','pending_review','approved','rejected','needs_change')"));
        word.HasKey(value => value.Id);
        word.HasIndex(value => value.ExternalId).IsUnique();
        word.HasIndex(value => value.Status);
        word.HasIndex(value => value.Cefr);
        word.HasIndex(value => value.PartOfSpeech);
        word.HasMany(value => value.Senses).WithOne().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        word.HasMany(value => value.Alternatives).WithOne().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        word.HasMany(value => value.Topics).WithOne().HasForeignKey(value => value.WordId).OnDelete(DeleteBehavior.Cascade);
        var sense = modelBuilder.Entity<Sense>();
        sense.ToTable("senses");
        sense.HasKey(value => value.Id);
        sense.HasIndex(value => new { value.WordId, value.Position }).IsUnique();
        var alternative = modelBuilder.Entity<Alternative>();
        alternative.ToTable("alternatives", table => table.HasCheckConstraint("ck_alternatives_kind", "kind IN ('synonym','near-synonym','different-sense','regional','register')"));
        alternative.HasKey(value => value.Id);
        alternative.HasIndex(value => new { value.WordId, value.Position }).IsUnique();
        var topic = modelBuilder.Entity<WordTopic>();
        topic.ToTable("word_topics");
        topic.HasKey(value => new { value.WordId, value.Topic });
        topic.HasIndex(value => new { value.Topic, value.WordId });
        foreach (var entity in new[] { word.Metadata, sense.Metadata, alternative.Metadata, topic.Metadata })
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(string.Concat(property.Name.Select((character, index) =>
                    char.IsUpper(character) && index > 0 ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString())));
            }
        }
    }

    // Single mandatory entry point for future learner-session selection. Catalog/reviewer reads are separate.
    internal static IQueryable<Word> WordsForSession(this PalabrasDbContext database) =>
        database.Words.AsNoTracking().Where(word => word.Status == "approved");
}
