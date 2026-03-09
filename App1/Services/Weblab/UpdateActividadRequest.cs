namespace Anfeta.UI.Models.Weblab
{
    public sealed class UpdateActividadRequest
    {
        public string? Titulo { get; set; }
        public string? Status { get; set; }
        public string? Prioridad { get; set; }
        public string? DueStart { get; set; }
        public string? DueEnd { get; set; }
        public string? Anotaciones { get; set; }
        public string? PasosYLinks { get; set; }
    }
}