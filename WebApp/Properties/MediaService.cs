using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Modelos;
using Data;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Xabe.FFmpeg;
using System.Drawing.Imaging;
using Microsoft.AspNetCore.Hosting;
using System.Text.RegularExpressions;
using System.Net.Http;

namespace Servicios
{
    public interface IMediaService
    {
        string GetThumbnail(string id);
        Task<MediaModel> GenerarMediaDesdeArchivo(IFormFile archivo);
        Task<MediaModel> GenerarMediaDesdeLink(string url);
        Task<bool> Eliminar(string id) ;
    }

    public class MediaService : IMediaService
    {
        private string CarpetaDeAlmacenamiento { get; }
        private readonly RChanContext context;
        private readonly IWebHostEnvironment env;

        public MediaService(string carpetaDeAlmacenamiento, RChanContext context, IWebHostEnvironment env)
        {
            this.CarpetaDeAlmacenamiento = carpetaDeAlmacenamiento;
            this.context = context;
            this.env = env;
        }

        public string GetThumbnail(string id)
        {
            var filenames = Directory
                .GetFileSystemEntries(Path.Join(CarpetaDeAlmacenamiento, "Thumbnails"))
                .Select(Path.GetFileName)
                .Select(e => $"/Thumbnails/{e}")
                .ToList();
            return Uri.EscapeUriString(filenames[new Random().Next(filenames.Count())]);
        }

        public async Task<MediaModel> GenerarMediaDesdeArchivo(IFormFile archivo)
        {
            bool esVideo = archivo.ContentType.Contains("video");

            using var archivoStream = archivo.OpenReadStream();

            Stream imagenStream;

            if(!esVideo)
                imagenStream = archivoStream;
            else
                imagenStream = await GenerarImagenDesdeVideo(archivoStream, archivo.FileName);

            // Genero un hash md5 del archivo
            archivoStream.Seek(0, SeekOrigin.Begin);
            var hash = await archivoStream.GenerarHashAsync();
            // Me fijo si el hash existe en la db
            var mediaAntiguo = await context.Medias.FirstOrDefaultAsync(e => e.Id == hash);

            if(mediaAntiguo != null) return mediaAntiguo;

            // Si no se resetea el stream imageSharp deja de funcionar -_o_-
            imagenStream.Seek(0, SeekOrigin.Begin);
            archivoStream.Seek(0, SeekOrigin.Begin);

            using var original = await Image.LoadAsync(imagenStream);
            using var thumbnail = original.Clone(e => e.Resize(300, 0));
            using var cuadradito = thumbnail.Clone(e => e.Resize( new ResizeOptions {
                Mode=ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
                Size = new Size(300)
            }));

            var media = new MediaModel
            {
                Id = hash,
                Hash = hash,
                Tipo = esVideo? MediaType.Video: MediaType.Imagen,
                Url = $"{hash}{Path.GetExtension(archivo.FileName)}",
            };

            // Si no se resetea el stream guarda correctamente -_o_-
            imagenStream.Seek(0, SeekOrigin.Begin);
            archivoStream.Seek(0, SeekOrigin.Begin);

            // Guardo las imagenes (original y miniatura) en el sistema de archivos
            using var salida = File.Create($"{CarpetaDeAlmacenamiento}/{media.Url}");
            await archivoStream.CopyToAsync(salida);
            await thumbnail.SaveAsync($"{CarpetaDeAlmacenamiento}/{media.VistaPrevia}");
            await cuadradito.SaveAsync($"{CarpetaDeAlmacenamiento}/{media.VistaPreviaCuadrado}");

            await imagenStream.DisposeAsync();
            await archivoStream.DisposeAsync();

            await context.Medias.AddAsync(media);

            return media;
        }
        public async Task<Stream> GenerarImagenDesdeVideo(Stream video, string filename)
        {
            //Guardo el archivo en una carpeta temporal
            var directoryTemporal =  Path.Join(env.ContentRootPath, "Temp");
            Directory.CreateDirectory(directoryTemporal);

            var archivoPath = Path.Join( directoryTemporal, Guid.NewGuid().ToString() + filename);

            video.Seek(0, SeekOrigin.Begin);
            var archivoGuardado = File.Create(archivoPath);
            await video.CopyToAsync(archivoGuardado);
            
            await archivoGuardado.DisposeAsync();

            var archivoSalidaThumbnail = Path.Join(directoryTemporal, Guid.NewGuid() + ".jpg");

            var conversion = await FFmpeg.Conversions.FromSnippet.Snapshot(archivoPath,
                    archivoSalidaThumbnail,
                    TimeSpan.FromSeconds(0));

            var result = await conversion.Start(); 

            var imgStream = new MemoryStream();

            var archivoTemporalImagen =  File.OpenRead(archivoSalidaThumbnail);
            await archivoTemporalImagen.CopyToAsync(imgStream);

            await archivoTemporalImagen.DisposeAsync();
            File.Delete(archivoPath);
            File.Delete(archivoSalidaThumbnail);
            return imgStream;
        }

        public async Task<MediaModel> GenerarMediaDesdeLink(string url) 
        {
            var match = Regex.Match(url, @"(?:youtube\.com\/\S*(?:(?:\/e(?:mbed))?\/|watch\?(?:\S*?&?v\=))|youtu\.be\/)([a-zA-Z0-9_-]{6,11})");
            if(!match.Success) return null;

            string id = match.Groups[1].Value;

            var mediaViejo = await context.Medias.FirstOrDefaultAsync(m => m.Hash == id);
            if(mediaViejo != null) return mediaViejo;

            var  vistaPrevia =  await new HttpClient().GetAsync($"https://img.youtube.com/vi/{id}/maxresdefault.jpg");

            if(!vistaPrevia.IsSuccessStatusCode)
            {
                vistaPrevia = await new HttpClient().GetAsync($"https://img.youtube.com/vi/{id}/hqdefault.jpg");
            }
            
            if(!vistaPrevia.IsSuccessStatusCode) return null;

            var media = new MediaModel {
                Id = id,
                Hash = id,
                Tipo = MediaType.Youtube,
                Url = url
            };

            using var thumbnail = await vistaPrevia.Content.ReadAsStreamAsync();

            using var cuadradito = (await Image.LoadAsync(thumbnail)).Clone(e => e.Resize( new ResizeOptions {
                Mode=ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
                Size = new Size(300)
            }));
            
            using var vistaPreviaSalida = File.Create($"{CarpetaDeAlmacenamiento}/{media.VistaPrevia}");
            thumbnail.Seek(0, SeekOrigin.Begin);

            await thumbnail.CopyToAsync(vistaPreviaSalida);
            await cuadradito.SaveAsync($"{CarpetaDeAlmacenamiento}/{media.VistaPreviaCuadrado}");
            
            return media;
        }

        public async Task<bool> Eliminar(string id) 
        {
            var intentos = 50;
            var media = await context.Medias.FirstOrDefaultAsync(m => m.Id == id);
            var archivosAEliminar = new List<string> (new[]{
                $"{CarpetaDeAlmacenamiento}/{media.VistaPreviaCuadrado}",
                $"{CarpetaDeAlmacenamiento}/{media.VistaPrevia}",
                $"{CarpetaDeAlmacenamiento}/{media.Url}",
            });

            media.Tipo = MediaType.Eliminado;
            media.Url = "";
            await context.SaveChangesAsync();
            while (archivosAEliminar.Count() != 0 || intentos == 0)
            {
                try
                {
                    File.Delete(archivosAEliminar.First());
                    archivosAEliminar.Remove(archivosAEliminar.First());
                }
                catch (Exception) { }
            
                await Task.Delay(200);
                intentos--;
            }
            return intentos == 0;
        }
    }
}

static class MediaExtension {
     public static async Task<string> GenerarHashAsync(this Stream archivo) {
            using var md5 = MD5.Create();
            var hash =  md5.ComputeHash(archivo);
            return string
                .Join("",hash.Select(e => e.ToString("x2")));
        }
}