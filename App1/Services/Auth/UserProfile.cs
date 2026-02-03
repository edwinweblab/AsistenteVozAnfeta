// Ubicación: Anfeta.UI/Services/Auth/UserProfile.cs
using System;

namespace Anfeta.UI.Services.Auth
{
    // Modelo con datos del perfil del usuario obtenidos del endpoint /api/auth/me
    // Campos: firstName, lastName, email, createdAt, updatedAt
    public sealed record UserProfile(
        string FirstName,
        string LastName,
        string Email,
        DateTime CreatedAt,
        DateTime UpdatedAt)
    {
        public string FullName => $"{FirstName} {LastName}".Trim();
    }
}