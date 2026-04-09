namespace VaultToFlashcard;

public class ProcessingSummary
{
    public int FilesProcessed { get; set; }
    public int NewFlashcards { get; set; }
    public int NotesMoved { get; set; }
    public int OrphanedNotesDeleted { get; set; }
    public int NotesSuspended { get; set; }
    public int NotesUnsuspended { get; set; }
    public int TotalFiles { get; set; }
    
    private readonly object _lock = new();

    public void Aggregate(ProcessingSummary? other)
    {
        if (other == null)
        {
            return;
        }
        
        lock (_lock)
        {
            FilesProcessed++;
            NewFlashcards += other.NewFlashcards;
            NotesMoved += other.NotesMoved;
        }
    }
}
