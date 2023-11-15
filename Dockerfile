# Run first: dotnet publish VLink.Private.GrpcRoomServer.csproj -c Release -o ./publish /p:UseAppHost=false
 
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app 
EXPOSE 5020

COPY ./publish .
ENTRYPOINT ["dotnet", "VLink.Private.GrpcRoomServer.dll"]
